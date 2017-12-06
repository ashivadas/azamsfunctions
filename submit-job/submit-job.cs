using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using System;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.MediaServices.Client;
using System.Threading.Tasks;
using System.IO;
using Microsoft.WindowsAzure.Storage;
using sharedutils;

namespace submitjob
{
    public  class SubmitJob
    {
        static MediaServiceHelper mediaServiceHelper = new MediaServiceHelper();

        [FunctionName("SubmitJob")]
        public  static async Task<HttpResponseMessage> RunAsync([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "HttpTriggerCSharp/name/{name}")]HttpRequestMessage req, string name, TraceWriter log)
        {
            log.Info("C# HTTP trigger function processed a request.");

            // Read values from the App.config file.
             string _storageAccountName = Environment.GetEnvironmentVariable("MediaServicesStorageAccountName");
             string _storageAccountKey = Environment.GetEnvironmentVariable("MediaServicesStorageAccountKey");
             string _AADTenantDomain = Environment.GetEnvironmentVariable("AMSAADTenantDomain");
             string _RESTAPIEndpoint = Environment.GetEnvironmentVariable("AMSRESTAPIEndpoint");
             string _mediaservicesClientId = Environment.GetEnvironmentVariable("AMSClientId");
             string _mediaservicesClientSecret = Environment.GetEnvironmentVariable("AMSClientSecret");
            
            // Field for cloud media context
            CloudMediaContext _context = null;
            CloudStorageAccount _destinationStorageAccount = null;

            int taskindex = 0;
            bool useEncoderOutputForAnalytics = false;
            IAsset outputEncoding = null;

            log.Info($"Webhook was triggered!");

            string jsonContent = await req.Content.ReadAsStringAsync();
            dynamic data = JsonConvert.DeserializeObject(jsonContent);

            log.Info(jsonContent);

            log.Info($"asset id : {data.assetId}");

            if (data.assetId == null)
            {
                // for test
                // data.assetId = "nb:cid:UUID:2d0d78a2-685a-4b14-9cf0-9afb0bb5dbfc";

                return req.CreateResponse(HttpStatusCode.BadRequest, new
                {
                    error = "Please pass asset ID in the input object (assetId)"
                });
            }

            // for test
            // data.WorkflowAssetId = "nb:cid:UUID:44fe8196-616c-4490-bf80-24d1e08754c5";
            // if data.WorkflowAssetId is passed, then it means a Premium Encoding task is asked

            log.Info($"Using Azure Media Service Rest API Endpoint : {_RESTAPIEndpoint}");

            IJob job = null;
            ITask taskEncoding = null;

            int OutputMES = -1;
            int OutputMEPW = -1;
            int OutputIndex1 = -1;
            int OutputIndex2 = -1;
            int OutputOCR = -1;
            int OutputFaceDetection = -1;
            int OutputMotion = -1;
            int OutputSummarization = -1;
            int OutputHyperlapse = -1;
            int OutputFaceRedaction = -1;
            int NumberJobsQueue = 0;

            try
            {
                AzureAdTokenCredentials tokenCredentials = new AzureAdTokenCredentials(_AADTenantDomain,
                                  new AzureAdClientSymmetricKey(_mediaservicesClientId, _mediaservicesClientSecret),
                                  AzureEnvironments.AzureCloudEnvironment);

                AzureAdTokenProvider tokenProvider = new AzureAdTokenProvider(tokenCredentials);

                _context = new CloudMediaContext(new Uri(_RESTAPIEndpoint), tokenProvider);


                // find the Asset
                string assetid = (string)data.assetId;
                IAsset asset = _context.Assets.Where(a => a.Id == assetid).FirstOrDefault();

                if (asset == null)
                {
                    log.Info($"Asset not found {assetid}");
                    return req.CreateResponse(HttpStatusCode.BadRequest, new
                    {
                        error = "Asset not found"
                    });
                }

                if (data.useEncoderOutputForAnalytics != null && (data.mesPreset != null || data.mesPreset != null))  // User wants to use encoder output for media analytics
                {
                    useEncoderOutputForAnalytics = (bool)data.useEncoderOutputForAnalytics;
                }


                // Declare a new encoding job with the Standard encoder
                int priority = 10;
                if (data.priority != null)
                {
                    priority = (int)data.priority;
                }
                job = _context.Jobs.Create(((string)data.jobName) ?? "Azure Functions Job", priority);

                if (data.mesPreset != null)  // MES Task
                {
                    // Get a media processor reference, and pass to it the name of the 
                    // processor to use for the specific task.
                    IMediaProcessor processorMES = mediaServiceHelper.GetLatestMediaProcessorByName("Media Encoder Standard", _context);

                    string preset = data.mesPreset;

                    if (preset.ToUpper().EndsWith(".JSON"))
                    {
                        // Change or modify the custom preset JSON used here.
                        //  preset = File.ReadAllText(@"D:\home\site\wwwroot\Presets\" + preset);

                        // Read in custom preset string
                        string homePath = Environment.GetEnvironmentVariable("HOME", EnvironmentVariableTarget.Process);
                        log.Info("Home= " + homePath);
                        string presetPath;

                        if (homePath == String.Empty)
                        {
                            presetPath = @"../presets/" + preset;
                        }
                        else
                        {
                            presetPath = Path.Combine(homePath, @"site\repository\media-functions-for-logic-app\presets\" + preset);
                        }
                        log.Info($"Preset path : {presetPath}");
                        preset = File.ReadAllText(presetPath);
                    }

                    // Create a task with the encoding details, using a string preset.
                    // In this case "H264 Multiple Bitrate 720p" system defined preset is used.
                    taskEncoding = job.Tasks.AddNew("MES encoding task",
                       processorMES,
                       preset,
                       TaskOptions.None);

                    // Specify the input asset to be encoded.
                    taskEncoding.InputAssets.Add(asset);
                    OutputMES = taskindex++;

                    // Add an output asset to contain the results of the job. 
                    // This output is specified as AssetCreationOptions.None, which 
                    // means the output asset is not encrypted. 
                    outputEncoding = taskEncoding.OutputAssets.AddNew(asset.Name + " MES encoded", AssetCreationOptions.None);
                }

                if (data.workflowAssetId != null)// Premium Encoder Task
                {

                    //find the workflow asset
                    string workflowassetid = (string)data.workflowAssetId;
                    IAsset workflowAsset = _context.Assets.Where(a => a.Id == workflowassetid).FirstOrDefault();

                    if (workflowAsset == null)
                    {
                        log.Info($"Workflow not found {workflowassetid}");
                        return req.CreateResponse(HttpStatusCode.BadRequest, new
                        {
                            error = "Workflow not found"
                        });
                    }

                    // Get a media processor reference, and pass to it the name of the 
                    // processor to use for the specific task.
                    IMediaProcessor processorMEPW = mediaServiceHelper.GetLatestMediaProcessorByName("Media Encoder Premium Workflow", _context);

                    string premiumConfiguration = "";
                    if (data.workflowConfig != null)
                    {
                        premiumConfiguration = (string)data.workflowConfig;
                    }
                    // In some cases, a configuration can be loaded and passed it to the task to tuned the workflow
                    // premiumConfiguration=File.ReadAllText(@"D:\home\site\wwwroot\Presets\SetRuntime.xml").Replace("VideoFileName", VideoFile.Name).Replace("AudioFileName", AudioFile.Name);

                    // Create a task
                    taskEncoding = job.Tasks.AddNew("Premium Workflow encoding task",
                       processorMEPW,
                       premiumConfiguration,
                       TaskOptions.None);

                    log.Info("task created");

                    // Specify the input asset to be encoded.
                    taskEncoding.InputAssets.Add(workflowAsset); // first add the Workflow
                    taskEncoding.InputAssets.Add(asset); // Then add the video asset
                    OutputMEPW = taskindex++;

                    // Add an output asset to contain the results of the job. 
                    // This output is specified as AssetCreationOptions.None, which 
                    // means the output asset is not encrypted. 
                    outputEncoding = taskEncoding.OutputAssets.AddNew(asset.Name + " Premium encoded", AssetCreationOptions.None);
                }

                IAsset an_asset = useEncoderOutputForAnalytics ? outputEncoding : asset;

                // Media Analytics
                JobHelpers helpers = new JobHelpers();
                OutputIndex1 = helpers.AddTask(job, an_asset, (string)data.indexV1Language, "Azure Media Indexer", "IndexerV1.xml", "English", ref taskindex, _context);
                OutputIndex2 = helpers.AddTask(job, an_asset, (string)data.indexV2Language, "Azure Media Indexer 2 Preview", "IndexerV2.json", "EnUs", ref taskindex, _context);
                OutputOCR = helpers.AddTask(job, an_asset, (string)data.ocrLanguage, "Azure Media OCR", "OCR.json", "AutoDetect", ref taskindex, _context);
                OutputFaceDetection = helpers.AddTask(job, an_asset, (string)data.faceDetectionMode, "Azure Media Face Detector", "FaceDetection.json", "PerFaceEmotion", ref taskindex, _context);
                OutputFaceRedaction = helpers.AddTask(job, an_asset, (string)data.faceRedactionMode, "Azure Media Redactor", "FaceRedaction.json", "combined", ref taskindex, _context);
                OutputMotion = helpers.AddTask(job, an_asset, (string)data.motionDetectionLevel, "Azure Media Motion Detector", "MotionDetection.json", "medium", ref taskindex, _context);
                OutputSummarization = helpers.AddTask(job, an_asset, (string)data.summarizationDuration, "Azure Media Video Thumbnails", "Summarization.json", "0.0", ref taskindex, _context);

                // Hyperlapse
                OutputHyperlapse = helpers.AddTask(job, asset, (string)data.hyperlapseSpeed, "Azure Media Hyperlapse", "Hyperlapse.json", "8", ref taskindex, _context);

                job.Submit();
                log.Info("Job Submitted");
                NumberJobsQueue = _context.Jobs.Where(j => j.State == JobState.Queued).Count();
            }
            catch (Exception ex)
            {
                log.Info($"Exception {ex}");
                return req.CreateResponse(HttpStatusCode.InternalServerError, new
                {
                    Error = ex.ToString()
                });
            }

            job = _context.Jobs.Where(j => j.Id == job.Id).FirstOrDefault(); // Let's refresh the job
            log.Info("Job Id: " + job.Id);
            log.Info("OutputAssetMESId: " + JobHelpers.ReturnId(job, OutputMES));
            log.Info("OutputAssetMEPWId: " + JobHelpers.ReturnId(job, OutputMEPW));
            log.Info("OutputAssetIndexV1Id: " + JobHelpers.ReturnId(job, OutputIndex1));
            log.Info("OutputAssetIndexV2Id: " + JobHelpers.ReturnId(job, OutputIndex2));
            log.Info("OutputAssetOCRId: " + JobHelpers.ReturnId(job, OutputOCR));
            log.Info("OutputAssetFaceDetectionId: " + JobHelpers.ReturnId(job, OutputFaceDetection));
            log.Info("OutputAssetFaceRedactionId: " + JobHelpers.ReturnId(job, OutputFaceRedaction));
            log.Info("OutputAssetMotionDetectionId: " + JobHelpers.ReturnId(job, OutputMotion));
            log.Info("OutputAssetSummarizationId: " + JobHelpers.ReturnId(job, OutputSummarization));
            log.Info("OutputAssetHyperlapseId: " + JobHelpers.ReturnId(job, OutputHyperlapse));

            return req.CreateResponse(HttpStatusCode.OK, new
            {
                jobId = job.Id,
                otherJobsQueue = NumberJobsQueue,
                mes = new
                {
                    assetId = JobHelpers.ReturnId(job, OutputMES),
                    taskId = JobHelpers.ReturnTaskId(job, OutputMES)
                },
                mepw = new
                {
                    assetId = JobHelpers.ReturnId(job, OutputMEPW),
                    taskId = JobHelpers.ReturnTaskId(job, OutputMEPW)
                },
                indexV1 = new
                {
                    assetId = JobHelpers.ReturnId(job, OutputIndex1),
                    taskId = JobHelpers.ReturnTaskId(job, OutputIndex1),
                    language = (string)data.indexV1Language
                },
                indexV2 = new
                {
                    assetId = JobHelpers.ReturnId(job, OutputIndex2),
                    taskId = JobHelpers.ReturnTaskId(job, OutputIndex2),
                    language = (string)data.indexV2Language
                },
                ocr = new
                {
                    assetId = JobHelpers.ReturnId(job, OutputOCR),
                    taskId = JobHelpers.ReturnTaskId(job, OutputOCR)
                },
                faceDetection = new
                {
                    assetId = JobHelpers.ReturnId(job, OutputFaceDetection),
                    taskId = JobHelpers.ReturnTaskId(job, OutputFaceDetection)
                },
                faceRedaction = new
                {
                    assetId = JobHelpers.ReturnId(job, OutputFaceRedaction),
                    taskId = JobHelpers.ReturnTaskId(job, OutputFaceRedaction)
                },
                motionDetection = new
                {
                    assetId = JobHelpers.ReturnId(job, OutputMotion),
                    taskId = JobHelpers.ReturnTaskId(job, OutputMotion)
                },
                summarization = new
                {
                    assetId = JobHelpers.ReturnId(job, OutputSummarization),
                    taskId = JobHelpers.ReturnTaskId(job, OutputSummarization)
                },
                hyperlapse = new
                {
                    assetId = JobHelpers.ReturnId(job, OutputHyperlapse),
                    taskId = JobHelpers.ReturnTaskId(job, OutputHyperlapse)
                }
            });

            // Fetching the name from the path parameter in the request URL
            return req.CreateResponse(HttpStatusCode.OK, "Hello " + name);
        }
    }
}
