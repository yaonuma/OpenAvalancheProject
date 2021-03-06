using System.IO;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Grib.Api;
using Microsoft.Azure.Management.DataLake.Store;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest.Azure.Authentication;
using System;
using Microsoft.Azure;
using System.Collections.Generic;
using System.Text;
using OpenAvalancheProject.Pipeline.Utilities;

using System.Reflection;
namespace OpenAvalancheProject.Pipeline
{
    public static class NAMBlobToTable
    {
        [FunctionName("NAMBlobToTable")]
        [return: Table("filedownloadtracker")]
        public static FileProcessedTracker Run([BlobTrigger("nam-grib-westus-v1/{name}", Connection = "AzureWebJobsStorage")]Stream myBlob, string name, TraceWriter log)
        {
            log.Info($"C# Blob trigger function Processed blob\n Name:{name} \n Size: {myBlob.Length} Bytes");
            log.Info($"Double Checking if {name} already exists.");
            var exists = AzureUtilities.CheckIfFileProcessedRowExistsInTableStorage(Constants.NamTrackerTable, Constants.NamTrackerPartitionKey, name, log);
            if (exists)
            {
                log.Info($"{name} Already exists in double check, skipping");
                return null;
            }
            log.Info($"Have env: {Environment.GetEnvironmentVariable("GRIB_API_DIR_ROOT")}");
            log.Info($"In dir: {Assembly.GetExecutingAssembly().Location}");
            string attemptPath = "";
            GribUtilities.TryFindBootstrapLibrary(out attemptPath);
            log.Info($"Attemping to find lib: {attemptPath}");
            GribEnvironment.Init();
#if DEBUG == false
            GribEnvironment.DefinitionsPath = @"D:\home\site\wwwroot\bin\Grib.Api\definitions";
#endif

            //1. Download stream to temp
            //TODO: there is supposedly now an ability to read a stream direction in GRIBAPI.Net; investigate to see if its better than storing a temp file
            string localFileName = AzureUtilities.DownloadBlobToTemp(myBlob, name, log);

            var rowList = new List<NamTableRow>();

            //2. Get values from file
            using (GribFile file = new GribFile(localFileName))
            {
                log.Info($"Parsing file {name}");
                rowList = GribUtilities.ParseNamGribFile(file);
            }

            //3. Format in correct table format
            log.Info($"Attempting to sign in to ad for datalake upload");
            var adlsAccountName = CloudConfigurationManager.GetSetting("ADLSAccountName");

            //auth secrets 
            var domain = CloudConfigurationManager.GetSetting("Domain");
            var webApp_clientId = CloudConfigurationManager.GetSetting("WebAppClientId");
            var clientSecret = CloudConfigurationManager.GetSetting("ClientSecret");
            var clientCredential = new ClientCredential(webApp_clientId, clientSecret);
            var creds = ApplicationTokenProvider.LoginSilentAsync(domain, clientCredential).Result;

            // Create client objects and set the subscription ID
            var adlsFileSystemClient = new DataLakeStoreFileSystemManagementClient(creds);
            try
            {
                adlsFileSystemClient.FileSystem.UploadFile(adlsAccountName, localFileName, "/nam-grib-westus-v1/" + name, uploadAsBinary: true, overwrite: true);
                log.Info($"Uploaded file: {localFileName}");
            }
            catch (Exception e)
            {
                log.Error($"Upload failed: {e.Message}");
            }

            MemoryStream s = new MemoryStream();
            StreamWriter csvWriter = new StreamWriter(s, Encoding.UTF8);
            csvWriter.WriteLine(NamTableRow.Columns);

            MemoryStream sLocations = new MemoryStream();
            StreamWriter csvLocationsWriter = new StreamWriter(sLocations, Encoding.UTF8);
            csvLocationsWriter.WriteLine("Lat, Lon");

            string fileName = null;
            foreach (var row in rowList)
            {
                if (fileName == null)
                {
                    fileName = row.PartitionKey + ".csv";
                }
                csvLocationsWriter.WriteLine(row.Lat + "," + row.Lon);
                csvWriter.WriteLine(row.ToString());
            }
            csvWriter.Flush();
            csvLocationsWriter.Flush();
            s.Position = 0;
            sLocations.Position = 0;

            AzureUtilities.UploadLocationsFile(sLocations, log);
            sLocations.Flush();
            sLocations.Close();

            log.Info($"Completed csv creation--attempting to upload to ADLS");

            try
            {
                adlsFileSystemClient.FileSystem.Create(adlsAccountName, "/nam-csv-westus-v1/" + fileName, s, overwrite: true);
                log.Info($"Uploaded csv stream: {localFileName}");
            }
            catch (Exception e)
            {
                log.Info($"Upload failed: {e.Message}");
            }

            s.Flush();
            s.Close();

            //delete local temp file
            File.Delete(localFileName);

            DateTime date = DateTime.ParseExact(name.Split('.')[0], "yyyyMMdd", null);
            return new FileProcessedTracker { ForecastDate = date, PartitionKey = "nam-grib-westus-v1", RowKey = name, Url = "unknown" };
        }
    }
}