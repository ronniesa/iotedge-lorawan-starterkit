//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Client;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using PacketManager;

namespace LoRaWan.NetworkServer
{
    public class LoraDeviceInfoManager
    {     
        public LoraDeviceInfoManager()
        {

        }

        public static async Task<LoraDeviceInfo> GetLoraDeviceInfoAsync(string DevAddr)
        {
            //todo return AppSKey
            bool returnAppSKey = true;
            if (DevAddr == null)
            {
                string errorMsg = "Missing devAddr in querystring";
                //log.Error(errorMsg);
                throw new Exception(errorMsg);
            }
    
            string connectionString = Environment.GetEnvironmentVariable("IoTHubConnectionString");
            if (connectionString == null)
            {
                string errorMsg = "Missing IoTHubConnectionString in settings";
                //log.Error(errorMsg);
                throw new Exception(errorMsg);
            }
            RegistryManager registryManager = RegistryManager.CreateFromConnectionString(connectionString);

            //Currently registry manager query only support select so we need to check for injection on the devaddr only for "'"
            //TODO check for sql injection
            DevAddr = DevAddr.Replace('\'', ' ');
            var query = registryManager.CreateQuery($"SELECT * FROM devices WHERE tags.DevAddr = '{DevAddr}'", 1);
            LoraDeviceInfo loraDeviceInfo = new LoraDeviceInfo();
            loraDeviceInfo.DevAddr = DevAddr;
            while (query.HasMoreResults)
            {
                var page = await query.GetNextAsTwinAsync();
                //we query only for 1 result 
                foreach (var twin in page)
                {
                    loraDeviceInfo.DevEUI = twin.DeviceId;
                    if (returnAppSKey)
                        loraDeviceInfo.AppSKey = twin.Tags["AppSKey"].Value;
                    loraDeviceInfo.NwkSKey = twin.Tags["NwkSKey"].Value;
                    if (twin.Tags.Contains("GatewayID"))
                        loraDeviceInfo.GatewayID = twin.Tags["GatewayID"].Value;
                    if (twin.Tags.Contains("SensorDecoder"))
                        loraDeviceInfo.SensorDecoder = twin.Tags["SensorDecoder"].Value;

                    if (twin.Tags.Contains("AppEUI"))
                        loraDeviceInfo.AppEUI = twin.Tags["AppEUI"].Value;
                    loraDeviceInfo.IsOurDevice = true;
                    if (twin.Properties.Reported.Contains("FCntUp"))
                        loraDeviceInfo.FCntUp = twin.Properties.Reported["FCntUp"];
                    if (twin.Properties.Reported.Contains("FCntDown"))
                    {
                        loraDeviceInfo.FCntDown = twin.Properties.Reported["FCntDown"];
                    }

                }
            }

            if (loraDeviceInfo.IsOurDevice)
            {
                var device = await registryManager.GetDeviceAsync(loraDeviceInfo.DevEUI);
                loraDeviceInfo.PrimaryKey = device.Authentication.SymmetricKey.PrimaryKey;
            }

            return loraDeviceInfo;

        }

        /// <summary>
        /// Code Performing the OTAA
        /// </summary>
        /// <param name="GatewayID"></param>
        /// <param name="DevEUI"></param>
        /// <param name="AppEUI"></param>
        /// <param name="DevNonce"></param>
        /// <returns></returns>
        public static async Task<LoraDeviceInfo> PerformOTAAAsync(string GatewayID, string DevEUI, string AppEUI, string DevNonce)
        {
            string AppKey;
            string AppSKey;
            string NwkSKey;
            string DevAddr;
            string AppNonce;
            //todo fix this 
            bool returnAppSKey = true;

            if (DevEUI == null || AppEUI == null || DevNonce == null)
            {
                string errorMsg = "Missing devEUI/AppEUI/DevNonce in the OTAARequest";
                //log.Error(errorMsg);
                throw new Exception(errorMsg);
            }


            var connectionString = Environment.GetEnvironmentVariable("IoTHubConnectionString");

            if (connectionString == null)
            {
                string errorMsg = "Missing IoTHubConnectionString in settings";
                //log.Error(errorMsg);
                throw new Exception(errorMsg);
            }
            RegistryManager registryManager = RegistryManager.CreateFromConnectionString(connectionString);
            LoraDeviceInfo loraDeviceInfo = new LoraDeviceInfo();
            loraDeviceInfo.DevEUI = DevEUI;
            var twin = await registryManager.GetTwinAsync(DevEUI);

            if (twin != null)
            {

                loraDeviceInfo.IsOurDevice = true;

                //Make sure that there is the AppEUI and it matches if not we cannot do the OTAA
                if (!twin.Tags.Contains("AppEUI"))
                {
                    string errorMsg = $"Missing AppEUI for OTAA for device {DevEUI}";
                    //log.Error(errorMsg);
                    throw new Exception(errorMsg);
                }
                else
                {
                    if (twin.Tags["AppEUI"].Value != AppEUI)
                    {
                        string errorMsg = $"AppEUI for OTAA does not match for device {DevEUI}";
                        //log.Error(errorMsg);
                        throw new Exception(errorMsg);
                    }
                }

                //Make sure that there is the AppKey if not we cannot do the OTAA
                if (!twin.Tags.Contains("AppKey"))
                {
                    string errorMsg = $"Missing AppKey for OTAA for device {DevEUI}";
                    //log.Error(errorMsg);
                    throw new Exception(errorMsg);
                }
                else
                {
                    AppKey = twin.Tags["AppKey"].Value;
                }

                //Make sure that is a new request and not a replay
                if (twin.Tags.Contains("DevNonce"))
                {
                    if (twin.Tags["DevNonce"] == DevNonce)
                    {
                        //this is a replay attack
                        string errorMsg = $"DevNonce already used for device {DevEUI}, potential replay attack";
                        Logger.Log(errorMsg, Logger.LoggingLevel.Info);
                        loraDeviceInfo.DevAddr = DevNonce;
                        loraDeviceInfo.IsJoinValid = false;
                        return loraDeviceInfo;
                    }
                }
                //Check that the device is joining throught the linked gateway and not another
                if (twin.Tags.Contains("GatewayID"))
                {
                    if (!String.IsNullOrEmpty(twin.Tags["GatewayID"].Value) && twin.Tags["GatewayID"].Value.ToUpper() != GatewayID.ToUpper())
                    {
                        string errorMsg = $"Not the right gateway device-gateway:{twin.Tags["GatewayID"].Value} current-gateway:{GatewayID}";
                        Logger.Log(errorMsg, Logger.LoggingLevel.Info);
                        loraDeviceInfo.DevAddr = DevNonce;
                        if (twin.Tags.Contains("GatewayID"))
                            loraDeviceInfo.GatewayID = twin.Tags["GatewayID"].Value;
                        loraDeviceInfo.IsJoinValid = false;
                        return loraDeviceInfo;
                    }

                }


                byte[] netId = new byte[3] { 0, 0, 1 };

                AppNonce = OTAAKeysGenerator.getAppNonce();

                AppSKey = OTAAKeysGenerator.calculateKey(new byte[1] { 0x02 }, OTAAKeysGenerator.StringToByteArray(AppNonce), netId, OTAAKeysGenerator.StringToByteArray(DevNonce), OTAAKeysGenerator.StringToByteArray(AppKey));
                NwkSKey = OTAAKeysGenerator.calculateKey(new byte[1] { 0x01 }, OTAAKeysGenerator.StringToByteArray(AppNonce), netId, OTAAKeysGenerator.StringToByteArray(DevNonce), OTAAKeysGenerator.StringToByteArray(AppKey)); ;



                //check that the devaddr is unique in the IoTHub registry
                bool isDevAddrUnique = false;

                do
                {
                    DevAddr = OTAAKeysGenerator.getDevAddr(netId);



                    var query = registryManager.CreateQuery($"SELECT * FROM devices WHERE tags.DevAddr = '{DevAddr}'", 1);
                    if (query.HasMoreResults)
                    {
                        var page = await query.GetNextAsTwinAsync();
                        if (!page.GetEnumerator().MoveNext())
                            isDevAddrUnique = true;
                        else
                            isDevAddrUnique = false;
                    }
                    else
                    {
                        isDevAddrUnique = true;
                    }

                } while (!isDevAddrUnique);



                var patch = new
                {
                    tags = new
                    {
                        AppSKey,
                        NwkSKey,
                        DevAddr,
                        DevNonce

                    }
                };

                await registryManager.UpdateTwinAsync(loraDeviceInfo.DevEUI, JsonConvert.SerializeObject(patch), twin.ETag);

                loraDeviceInfo.DevAddr = DevAddr;
                loraDeviceInfo.AppKey = twin.Tags["AppKey"].Value;
                loraDeviceInfo.NwkSKey = NwkSKey;
                loraDeviceInfo.AppSKey = AppSKey;
                loraDeviceInfo.AppNonce = AppNonce;
                loraDeviceInfo.AppEUI = AppEUI;
                loraDeviceInfo.NetId = BitConverter.ToString(netId).Replace("-", ""); ;

                if (!returnAppSKey)
                    loraDeviceInfo.AppSKey = null;

                //Accept the JOIN Request and the futher messages
                loraDeviceInfo.IsJoinValid = true;

                var device = await registryManager.GetDeviceAsync(loraDeviceInfo.DevEUI);
                loraDeviceInfo.PrimaryKey = device.Authentication.SymmetricKey.PrimaryKey;

                if (twin.Tags.Contains("GatewayID"))
                    loraDeviceInfo.GatewayID = twin.Tags["GatewayID"].Value;
                if (twin.Tags.Contains("SensorDecoder"))
                    loraDeviceInfo.SensorDecoder = twin.Tags["SensorDecoder"].Value;


            }
            else
            {
                loraDeviceInfo.IsOurDevice = false;
            }


            return loraDeviceInfo;
        }
    }

  


}