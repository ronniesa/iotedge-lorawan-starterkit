//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//
using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using System;
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Shared;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using System.Text;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.WebUtilities;

namespace CreateDeviceFunction
{
    class HostSecrets
    {
        [JsonProperty(PropertyName = "masterKey")]
        public Key MasterKey { get; set; }

        [JsonProperty(PropertyName = "functionKeys")]
        public IList<Key> FunctionKeys { get; set; }

        [JsonProperty(PropertyName = "systemKeys")]
        public IList<Key> SystemKeys { get; set; }
    }

    public static class CreateEdgeDevice
    {
        private static ServiceProvider serviceProvider;

        static CreateEdgeDevice()
        {
            var services = new ServiceCollection();
            services.AddDataProtection();
            
            serviceProvider = services.BuildServiceProvider();
        }


        [FunctionName("CreateEdgeDevice")]
        public static async System.Threading.Tasks.Task<HttpResponseMessage> RunAsync([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req, TraceWriter log, ExecutionContext context)
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
            string connectionString = config.GetConnectionString("IoTHubConnectionString");
            string deviceConfigurationUrl = Environment.GetEnvironmentVariable("DEVICE_CONFIG_LOCATION");
            RegistryManager manager = RegistryManager.CreateFromConnectionString(connectionString);
            // parse query parameter
            var queryStrings=req.GetQueryParameterDictionary();
            string deviceName = "";


            queryStrings.TryGetValue("deviceName", out deviceName);

            string facadeKey = "";
            string storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            Key key = null;
            CloudStorageAccount account = CloudStorageAccount.Parse(storageConnectionString);
            CloudBlobClient serviceClient = account.CreateCloudBlobClient();
            // point to the function storage container holding the keys.
           
            var container = serviceClient.GetContainerReference("azure-webjobs-secrets");
            await container.CreateIfNotExistsAsync();
            BlobContinuationToken blobContinuationToken = null;
            do
            {
                var results = await container.ListBlobsSegmentedAsync(null, blobContinuationToken);
                // Get the value of the continuation token returned by the listing call.
                blobContinuationToken = results.ContinuationToken;
                foreach (IListBlobItem item in results.Results)
                {
                    Console.WriteLine(item.Uri);
                }
            } while (blobContinuationToken != null); // Loop while the continuation token is not null.
            CloudBlob blob = container.GetBlobReference(Environment.GetEnvironmentVariable("WEBSITE_CONTENTSHARE") + "/host.json");
            using (var stream = new MemoryStream())
            {
                await blob.DownloadToStreamAsync(stream);
                stream.Position = 0;//resetting stream's position to 0
                var serializer = new JsonSerializer();
               
                using (var sr = new StreamReader(stream))
                {
                    using (var jsonTextReader = new JsonTextReader(sr))
                    {
                        	var host = JsonConvert.DeserializeObject<HostSecrets>(File.ReadAllText(@"D:\home\data\Functions\secrets\host.json"));
                        HostSecrets result = serializer.Deserialize<HostSecrets>(jsonTextReader);
                        key=result.MasterKey;
                    }
                }
            }

             var provider= Microsoft.Azure.Web.DataProtection.DataProtectionProvider.CreateAzureDataProtector();
            var dataProtector = provider.CreateProtector("function-secrets");
            DataProtectionKeyValueConverter converter = new DataProtectionKeyValueConverter(FileAccess.Read);
            if (key.IsEncrypted)
            {
                key = converter.ReadValue(key);
            }
            //var protector =  _dataProtector as IPersistedDataProtector;
            //var provider = Microsoft.Azure.Web.DataProtection.DataProtectionProvider.CreateAzureDataProtector();
            //_dataProtector = provider.CreateProtector("function-secrets");
            if (dataProtector != null)
            {
                byte[] data = WebEncoders.Base64UrlDecode(facadeKey);
                byte[] result = dataProtector.Unprotect(data);
               

                facadeKey = Encoding.UTF8.GetString(result);
            
            }
            else
            {
               facadeKey = dataProtector.Unprotect(facadeKey);
            }

            Device edgeGatewayDevice = new Device(deviceName);
            edgeGatewayDevice.Capabilities = new DeviceCapabilities()
            {
                IotEdge = true
            };
            await manager.AddDeviceAsync(edgeGatewayDevice);
            string json = "";
            //todo correct
            using (WebClient wc = new WebClient())
            {
                 json = wc.DownloadString(deviceConfigurationUrl);
            }
            ConfigurationContent spec = JsonConvert.DeserializeObject<ConfigurationContent>(json);
            await manager.AddModuleAsync(new Module(deviceName, "LoRaWanNetworkSrvModule"));
            await manager.ApplyConfigurationContentOnDeviceAsync(deviceName, spec);

            Twin twin = new Twin();
            twin.Properties.Desired = new TwinCollection(@"{FacadeServerUrl:'" + String.Format("https://{0}.azurewebsites.net/api/", GetEnvironmentVariable("FACADE_HOST_NAME")) + "',FacadeAuthCode: " +
                "'" + facadeKey + "'}");
            var remoteTwin = await manager.GetTwinAsync(deviceName);

            await manager.UpdateTwinAsync(deviceName, "LoRaWanNetworkSrvModule", twin, remoteTwin.ETag);

            bool deployEndDevice = false;
            Boolean.TryParse(Environment.GetEnvironmentVariable("DEPLOY_DEVICE"),out deployEndDevice);

            //This section will get deployed ONLY if the user selected the "deploy end device" options.
            //Information in this if clause, is for demo purpose only and should not be used for productive workloads.
            if (deployEndDevice)
            {
                Device endDevice = new Device("47AAC86800430028");
                await manager.AddDeviceAsync(endDevice);
                Twin endTwin = new Twin();
                endTwin.Tags = new TwinCollection(@"{AppEUI:'BE7A0000000014E2',AppKey:'8AFE71A145B253E49C3031AD068277A1',GatewayID:''," +
                "SensorDecoder:'DecoderValueSensor'}");
    
                var endRemoteTwin = await manager.GetTwinAsync(deviceName);
                await manager.UpdateTwinAsync("47AAC86800430028", endTwin, endRemoteTwin.ETag);

            }


            var template = @"{'$schema': 'https://schema.management.azure.com/schemas/2015-01-01/deploymentTemplate.json#', 'contentVersion': '1.0.0.0', 'parameters': {}, 'variables': {}, 'resources': []}";
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
            Console.WriteLine(template);

            response.Content = new StringContent(template, System.Text.Encoding.UTF8, "application/json");

            return response;
        }

        public static string GetEnvironmentVariable(string name)
        {
            return
                System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        }

    }


}




