﻿using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Common.Exceptions;
using Microsoft.Azure.Devices.Provisioning.Service;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Azure.DigitalTwins.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Portal.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Portal.Helper
{
    public interface IIoTHubHelper
    {
        Task<IEnumerable<SelectListItem>> GetIoTHubDevices();
        Task<Device> GetIoTHubDevice(string deviceId);
        Task<bool> AddIoTHubDevice(string deviceId);
        Task<bool> DeleteIoTHubDevice(string deviceId);
        Task<Twin> GetDeviceTwin(string deviceId);
        Task<IEnumerable<SelectListItem>> GetDpsEnrollments();
        Task<IndividualEnrollment> GetDpsEnrollment(string registrationId);
        Task<AttestationMechanism> GetDpsAttestationMechanism(string registrationId);
        Task<bool> AddDpsEnrollment(string newRegistrationId);
        Task<bool> AddDpsGroupEnrollment(StringBuilder certContents);
        Task<bool> AddDpsGroupEnrollment(string certPath);
        Task<bool> DeleteDpsEnrollment(string registrationId);
        Task<CloudToDeviceMethodResult> SendMethod(string deviceId, string command, string payload);
        string GetIoTHubName(string deviceConnectionString);
    }
    public class IoTHubHelper : IIoTHubHelper
    {
        private readonly RegistryManager _registryManager;
        private readonly ILogger<IoTHubHelper> _logger;
        private readonly AppSettings _appSettings;
        private DeviceClient _deviceClient;
        private bool _isConnected;
        private readonly ServiceClient _serviceClient;
        private readonly DigitalTwinClient _digitalTwinClient;
        private readonly ProvisioningServiceClient _provisioningServiceClient;
        private readonly string _dps_webhookUrl;
        private readonly string _privateModelRepoUrl;
        private readonly string _privateModelToken;

        public IoTHubHelper(IOptions<AppSettings> config, ILogger<IoTHubHelper> logger)
        {
            _logger = logger;
            _appSettings = config.Value;
            _registryManager = RegistryManager.CreateFromConnectionString(_appSettings.IoTHub.ConnectionString);
            _serviceClient = ServiceClient.CreateFromConnectionString(_appSettings.IoTHub.ConnectionString);
            _digitalTwinClient = DigitalTwinClient.CreateFromConnectionString(_appSettings.IoTHub.ConnectionString);
            _provisioningServiceClient = ProvisioningServiceClient.CreateFromConnectionString(_appSettings.Dps.ConnectionString);
            _dps_webhookUrl = _appSettings.Dps.WebHookUrl;
            _deviceClient = null;
            _isConnected = false;
            _privateModelRepoUrl = _appSettings.ModelRepository.repoUrl;
            _privateModelToken = _appSettings.GitHub.token;
        }

        #region IOTHUB
        /**********************************************************************************
         * Get list of devices from IoT Hub
         *********************************************************************************/
        public async Task<IEnumerable<SelectListItem>> GetIoTHubDevices()
        {
            List<SelectListItem> deviceList = new List<SelectListItem>();
            // add empty one
            deviceList.Add(new SelectListItem { Value = "", Text = "" });

            try
            {
                IQuery query = _registryManager.CreateQuery("select * from devices");

                while (query.HasMoreResults)
                {
                    var twins = await query.GetNextAsTwinAsync().ConfigureAwait(false);
                    foreach (var twin in twins)
                    {
                        _logger.LogInformation($"Found a device : {twin.DeviceId}");
                        deviceList.Add(new SelectListItem { Value = twin.DeviceId, Text = twin.DeviceId });
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception in GetIoTHubDevices() : {e.Message}");
            }

            return deviceList;
        }

        /**********************************************************************************
         * Retrieves the specified Device object.
         *********************************************************************************/
        public async Task<Device> GetIoTHubDevice(string deviceId)
        {
            Device device = null;
            try
            {
                device = await _registryManager.GetDeviceAsync(deviceId);
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception in GetIoTHubDevice() : {e.Message}");
            }
            return device;
        }

        /**********************************************************************************
         * Register a new device with IoT Hub
         *********************************************************************************/
        public async Task<bool> AddIoTHubDevice(string deviceId)
        {
            Device device = null;
            bool bCreated = false;

            try
            {
                device = await _registryManager.GetDeviceAsync(deviceId.ToString());

                if (device == null)
                {
                    _logger.LogDebug($"Creating a new device : '{deviceId}'");
                    device = await _registryManager.AddDeviceAsync(new Device(deviceId.ToString()));
                    bCreated = true;
                }
                else
                {
                    _logger.LogWarning($"Device already exist : '{deviceId}'");
                }
            }
            catch (DeviceAlreadyExistsException)
            {
                _logger.LogWarning($"Exception Device already exist : '{deviceId}'");
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception in AddIoTHubDevice() : {e.Message}");
            }
            return bCreated;
        }

        /**********************************************************************************
         * Deletes a previously registered device from IoT Hub
         *********************************************************************************/
        public async Task<bool> DeleteIoTHubDevice(string deviceId)
        {
            try
            {
                _logger.LogDebug($"Removing {deviceId}");
                await _registryManager.RemoveDeviceAsync(deviceId.ToString());
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception in DeleteIoTHubDevice() : {e.Message}");
                return false;
            }
            return true;
        }

        /**********************************************************************************
         * Gets Twin from IoT Hub
         *********************************************************************************/
        public async Task<Twin> GetDeviceTwin(string deviceId)
        {
            Twin twin = null;

            try
            {
                _logger.LogDebug($"Retrieving Twin for {deviceId}");
                twin = await _registryManager.GetTwinAsync(deviceId);
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception in GetDeviceTwin() : {e.Message}");
            }
            return twin;
        }

        #endregion // IOTHUB

        #region DPS
        /**********************************************************************************
         * Get list of individual entrollments from DPS
         *********************************************************************************/
        public async Task<IEnumerable<SelectListItem>> GetDpsEnrollments()
        {
            List<SelectListItem> enrollmentList = new List<SelectListItem>();
            // add empty one
            enrollmentList.Add(new SelectListItem { Value = "", Text = "" });

            try
            {
                QuerySpecification querySpecification = new QuerySpecification("SELECT * FROM enrollments");
                using (Query query = _provisioningServiceClient.CreateIndividualEnrollmentQuery(querySpecification))
                {
                    while (query.HasNext())
                    {
                        QueryResult queryResult = await query.NextAsync().ConfigureAwait(false);
                        foreach (IndividualEnrollment enrollment in queryResult.Items)
                        {
                            // we only support symmetric key for now
                            if (enrollment.Attestation.GetType().Name.Equals("SymmetricKeyAttestation"))
                            {
                                enrollmentList.Add(new SelectListItem { Value = enrollment.RegistrationId, Text = enrollment.RegistrationId });
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception in GetDpsEnrollments() : {e.Message}");
            }

            return enrollmentList;
        }

        /**********************************************************************************
         * Retrive Individual Enrollment from DPS
         *********************************************************************************/
        public async Task<IndividualEnrollment> GetDpsEnrollment(string registrationId)
        {
            IndividualEnrollment enrollment = null;

            try
            {
                QuerySpecification querySpecification = new QuerySpecification("SELECT * FROM enrollments");

                using (Query query = _provisioningServiceClient.CreateIndividualEnrollmentQuery(querySpecification))
                {
                    while (query.HasNext() && enrollment == null)
                    {
                        QueryResult queryResults = await query.NextAsync().ConfigureAwait(false);

                        foreach (IndividualEnrollment item in queryResults.Items)
                        {
                            _logger.LogInformation($"GetDpsEnrollment found enrollment : {item}");

                            if (item.RegistrationId.Equals(registrationId))
                            {
                                enrollment = item;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception in GetDpsEnrollment() : {e.Message}");
            }

            return enrollment;
        }

        /**********************************************************************************
         * Retrieve attestation from DPS
         *********************************************************************************/
        public async Task<AttestationMechanism> GetDpsAttestationMechanism(string registrationId)
        {
            AttestationMechanism attestation = null;

            try
            {
                attestation = await _provisioningServiceClient.GetIndividualEnrollmentAttestationAsync(registrationId).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception in GetDpsEnrollment() : {e.Message}");
            }

            return attestation;
        }

        /**********************************************************************************
         * Add a new individual enrollment
         *********************************************************************************/
        public async Task<bool> AddDpsEnrollment(string newRegistrationId)
        {
            bool bCreated = false;
            string primaryKey = "";
            string secondaryKey = "";

            try
            {
                Attestation attestation = new SymmetricKeyAttestation(primaryKey, secondaryKey);
                IndividualEnrollment individualEnrollment = new IndividualEnrollment(newRegistrationId, attestation);
                individualEnrollment.DeviceId = newRegistrationId;
                if (!string.IsNullOrEmpty(_dps_webhookUrl))
                {
                    individualEnrollment.CustomAllocationDefinition = new CustomAllocationDefinition() { WebhookUrl = _dps_webhookUrl, ApiVersion = "2019-03-31" };
                    individualEnrollment.AllocationPolicy = AllocationPolicy.Custom;
                }
                var newEnrollment = await _provisioningServiceClient.CreateOrUpdateIndividualEnrollmentAsync(individualEnrollment).ConfigureAwait(false);

                bCreated = true;
            }
            //catch (DeviceAlreadyExistsException)
            //{
            //}
            catch (Exception e)
            {
                _logger.LogError($"Exception in GetDpsEnrollment() : {e.Message}");
            }
            return bCreated;
        }

        /**********************************************************************************
         * Deletes enrollment from DPS
         *********************************************************************************/
        public async Task<bool> DeleteDpsEnrollment(string registrationId)
        {
            bool bDeleted = false;
            try
            {
                _logger.LogDebug($"Removing enrollment {registrationId}");
                await _provisioningServiceClient.DeleteIndividualEnrollmentAsync(registrationId).ConfigureAwait(false);
                bDeleted = true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception in DeleteDpsEnrollment() : {e.Message}");
            }
            return bDeleted;
        }

        /**********************************************************************************
         * Upload CA for proof of possession
         *********************************************************************************/
        public async Task<bool> AddDpsGroupEnrollment(string certPath)
        {
            try
            {
                var certificate = new X509Certificate2(certPath);
                Attestation attestation = X509Attestation.CreateFromRootCertificates(certificate);
                EnrollmentGroup enrollmentGroup =
                    new EnrollmentGroup(
                            "Test",
                            attestation)
                    {
                        ProvisioningStatus = ProvisioningStatus.Enabled
                    };

                EnrollmentGroup enrollmentGroupResult =
                await _provisioningServiceClient.CreateOrUpdateEnrollmentGroupAsync(enrollmentGroup).ConfigureAwait(false);
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception in DeleteDpsEnrollment() : {e.Message}");
            }
            return false;
        }
        public async Task<bool> AddDpsGroupEnrollment(StringBuilder certContents)
        {
            try
            {
                byte[] byteArray = ASCIIEncoding.ASCII.GetBytes(certContents.ToString());
                var certificate = new X509Certificate2(byteArray);
                Attestation attestation = X509Attestation.CreateFromRootCertificates(certificate);
                EnrollmentGroup enrollmentGroup =
                    new EnrollmentGroup(
                            "Test",
                            attestation)
                    {
                        ProvisioningStatus = ProvisioningStatus.Enabled
                    };

                EnrollmentGroup enrollmentGroupResult =
                await _provisioningServiceClient.CreateOrUpdateEnrollmentGroupAsync(enrollmentGroup).ConfigureAwait(false);
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Exception in DeleteDpsEnrollment() : {e.Message}");
            }
            return false;
        }

        #endregion



        public async Task<Twin> SetModelId(string deviceConnectionString, string deviceModelId)
        {
            Twin twin = null;
            int retry = 10;

            var options = new ClientOptions
            {
                ModelId = deviceModelId,
            };

            if (_deviceClient == null)
            {
                _deviceClient = DeviceClient.CreateFromConnectionString(deviceConnectionString, Microsoft.Azure.Devices.Client.TransportType.Mqtt, options);
                _deviceClient.SetConnectionStatusChangesHandler(ConnectionStatusChangedHandler);
                await _deviceClient.OpenAsync();

                while (_isConnected == false && retry > 0)
                {
                    await Task.Delay(1000);
                    retry--;
                }

                twin = await _deviceClient.GetTwinAsync();
            }
            else
            {
                twin = await _deviceClient.GetTwinAsync();
                await _deviceClient.CloseAsync();
                _deviceClient.Dispose();
                _deviceClient = null;
            }

            return twin;
        }

        /**********************************************************************************
         * Get a name of IoT Hub from Connection String
         *********************************************************************************/
        public string GetIoTHubName(string deviceConnectionString)
        {
            return deviceConnectionString.Split(';')[0].Split('=')[1];
        }

        /**********************************************************************************
         * Connection Status Change Notification for Web Client simulator
         *********************************************************************************/
        private void ConnectionStatusChangedHandler(
                    ConnectionStatus status,
                    ConnectionStatusChangeReason reason)
        {
            _logger.LogInformation(
                "Client connection state changed. Status: {status}, Reason: {reason}",
                status,
                reason);

            if (status == ConnectionStatus.Connected)
            {
                _isConnected = true;
            }
            else
            {
                _isConnected = false;
            }
        }

        public async Task<CloudToDeviceMethodResult> SendMethod(string deviceId, string command, string payload)
        {
            JObject cmdPayload = JObject.Parse(payload);

            if (_serviceClient != null)
            {
                var methodInvocation = new CloudToDeviceMethod(command)
                {
                    ResponseTimeout = TimeSpan.FromSeconds(30)
                };

                Twin deviceTwin = await GetDeviceTwin(deviceId);

                if (!string.IsNullOrEmpty(deviceTwin.ModelId))
                {
                    DTDLModelResolver resolver = new DTDLModelResolver(_privateModelRepoUrl, _privateModelToken, _logger);

                    var modelData = await resolver.ParseModelAsync(deviceTwin.ModelId);

                    var interfaces = modelData.Where(r => r.Value.EntityKind == DTEntityKind.Command).ToList();

                    //var commands = interfaces.Select(r => r.Value as DTCommandInfo).Where(x => x.Name == command).ToList();
                    var dtCmd = interfaces.Select(r => r.Value as DTCommandInfo).Single(x => x.Name == command);

                    if (cmdPayload.ContainsKey(dtCmd.Request.Name))
                    {
                        switch (dtCmd.Request.Schema.EntityKind)
                        {
                            case DTEntityKind.String:
                                methodInvocation.SetPayloadJson(new string(cmdPayload[dtCmd.Request.Name].ToString()));
                                break;

                            case DTEntityKind.Integer:
                                if (cmdPayload[dtCmd.Request.Name].Type == JTokenType.String)
                                {
                                    var value = cmdPayload[dtCmd.Request.Name].ToString();

                                    // convert to integer
                                    Regex rx = new Regex(@"^[0-9]+$");

                                    if (!rx.IsMatch(value))
                                    {
                                        var result = new CloudToDeviceMethodResult()
                                        {
                                            Status = 400,
                                        };
                                        return result;
                                    }
                                    else
                                    {
                                        methodInvocation.SetPayloadJson(Int32.Parse(value).ToString());
                                    }
                                }
                                break;

                            case DTEntityKind.Float:
                            case DTEntityKind.Double:
                                break;
                        }
                        //cmdPayload.
                    }
                }

                try
                {
                    var response = await _serviceClient.InvokeDeviceMethodAsync(deviceId, methodInvocation);
                    Console.WriteLine("Response status: {0}, payload:", response.Status);
                    Console.WriteLine(response.GetPayloadAsJson());
                    return response;
                }
                catch (Exception e)
                {
                    _logger.LogError($"Exception in GetIoTHubDevice() : {e.Message}");
                }
            }
            return null;
        }
    }
}
