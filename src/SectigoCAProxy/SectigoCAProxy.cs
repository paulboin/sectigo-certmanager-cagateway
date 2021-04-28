﻿// Copyright 2021 Keyfactor
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions
// and limitations under the License.
using CAProxy.AnyGateway;
using CAProxy.AnyGateway.Interfaces;
using CAProxy.AnyGateway.Models;
using CAProxy.Common;
using Common.Logging;
using CSS.Common.Logging;
using CSS.PKI;
using Keyfactor.AnyGateway.Sectigo.API;
using Keyfactor.AnyGateway.Sectigo.Client;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Keyfactor.AnyGateway.Sectigo
{
    /// <summary>
    /// Implementation of the <see cref="BaseCAConnector"/> for Secitgo Certificate Manager. This class contains the 
    /// entry points for AnyGateway Synchronization, Revocation, and Enrollment functions. 
    /// </summary>
    public class SectigoCAProxy : BaseCAConnector
    {

        const int PROFILE_RENEW_ERROR_CODE = -8408;
        /// <summary>
        /// CAConnection section of the imported AnyGateway configuration 
        /// </summary>
        SectigoCAConfig Config { get; set; }

        /// <summary>
        /// API Implementation for AnyGateway to interact with the Sectigo Certificate Manager API
        /// </summary>
        SectigoApiClient Client { get; set; }

        /// <summary>
        /// Method to query, parse, and return certificates to be synchronized with the AnyGateway database
        /// </summary>
        /// <param name="certificateDataReader">Interface to access existing certificate data from the Gateway database</param>
        /// <param name="blockingBuffer">A <see cref="BlockingCollection{T}"/> to queue certificates for synchronization</param>
        /// <param name="certificateAuthoritySyncInfo">Details about previous synchronization attempts</param>
        /// <param name="cancelToken"></param>
        public override void Synchronize(ICertificateDataReader certificateDataReader,
                                         BlockingCollection<CAConnectorCertificate> blockingBuffer,
                                         CertificateAuthoritySyncInfo certificateAuthoritySyncInfo,
                                         CancellationToken cancelToken)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);

            Task producerTask = null;

            CancellationTokenSource newCancelToken = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);

            try
            {
                var certsToAdd = new BlockingCollection<Certificate>(100);

                producerTask = Client.CertificateListProducer(certsToAdd, newCancelToken.Token, Config.PageSize, Config.GetSyncFilterQueryString());

                foreach (Certificate certToAdd in certsToAdd.GetConsumingEnumerable())
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        Logger.Warn($"Task was cancelled. Stopping synchronize task.");
                        blockingBuffer.CompleteAdding();
                        break;
                    }

                    if (producerTask.Exception != null)
                    {
                        Logger.Error($"Sync task failed with the following message: {producerTask.Exception.Flatten().Message}");
                        throw producerTask.Exception.Flatten();
                    }

                    CAConnectorCertificate dbCert=null;
                    if(!String.IsNullOrEmpty(certToAdd.SerialNumber))
                        dbCert = certificateDataReader.GetCertificateRecord(CSS.Common.DataConversion.HexToBytes(certToAdd.SerialNumber));
                    //we found an existing cert from the DB by serial number.  This should already be in the DB so no need to sync again

                    //are we syncing a reissued cert? Reissued certs keep the same ID, but may have different data and cause index errors on sync
                    int syncReqId = 0;
                    if (dbCert != null && dbCert.CARequestID.Contains('-'))
                    {
                        syncReqId = int.Parse(dbCert.CARequestID.Split('-')[0]);
                    }
                    else if (dbCert != null)
                    {
                        syncReqId = int.Parse(dbCert.CARequestID);
                    }

                    string certData = string.Empty;
                    if (dbCert != null)
                    {
                        if (dbCert.Status == ConvertToKeyfactorStatus(certToAdd.status))
                        {
                            Logger.Info($"Certificate {certToAdd.CommonName} (ID: {certToAdd.Id}) with SN: {certToAdd.SerialNumber} already synced. Skipping.");
                            continue;
                        }

                        Logger.Info($"Certificate status changed.  Syncing new status");
                        certData = dbCert.Certificate;

                    }
                    else
                    {
                        //No certificate in the DB by SN.  Need to download to get full certdata required for sync process
                        Logger.Trace($"Attempt to Pickup Certificate {certToAdd.CommonName} (ID: {certToAdd.Id})");
                        var certdataApi = Task.Run(async () => await Client.PickupCertificate(certToAdd.Id, certToAdd.CommonName)).Result;
                        certData = Convert.ToBase64String(certdataApi.GetRawCertData());
                    }

                    if (certToAdd == null || String.IsNullOrEmpty(certToAdd.SerialNumber) || String.IsNullOrEmpty(certToAdd.CommonName) || String.IsNullOrEmpty(certData))
                    {
                        Logger.Trace($"Certificate Data unavailable from Sectigo for {certToAdd.CommonName} (ID: {certToAdd.Id}). Skipping ");
                        continue;
                    }

                    CAConnectorCertificate caCertToAdd = new CAConnectorCertificate
                    {
                        CARequestID = syncReqId == 0 ? certToAdd.Id.ToString() : syncReqId.ToString(),
                        ProductID = certToAdd.CertType.id.ToString(),
                        Certificate = certData,
                        Status = ConvertToKeyfactorStatus(certToAdd.status),
                        SubmissionDate = certToAdd.requested,
                        ResolutionDate = certToAdd.approved,
                        RevocationReason = ConvertToKeyfactorStatus(certToAdd.status) == 21 ? 0 : 0xffffff,
                        RevocationDate = certToAdd.revoked,
                    };

                    if (blockingBuffer.TryAdd(caCertToAdd, 50, cancelToken))
                    {
                        Logger.Trace($"Added {certToAdd.CommonName} (ID:{(syncReqId == 0 ? certToAdd.Id.ToString() : syncReqId.ToString())}) to queue for synchronization");
                    }
                    else
                    {
                        Logger.Trace($"Adding {certToAdd.CommonName} to queue was blocked. Retrying");
                    }
                }
                blockingBuffer.CompleteAdding();
            }
            catch (Exception ex)
            {
                //gracefully exit so any certs added to queue prior to failure will still sync
                Logger.Error($"Sync failed. {ex.Message}");
                if (producerTask != null && !producerTask.IsCompleted)
                {
                    newCancelToken.Cancel();
                }

                blockingBuffer.CompleteAdding();
            }
            Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
        }
        
        /// <summary>
        /// Method to execute a single certificate sync after enrollment or revocation. Used to update status of a single record in Command
        /// </summary>
        /// <param name="caRequestID">The certificate's unique ID as defined by the syncronization/enroll methods</param>
        /// <returns></returns>
        public override CAConnectorCertificate GetSingleRecord(string caRequestID)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);


            int reqId;

            if (caRequestID.Contains('-'))
            {
                reqId = int.Parse(caRequestID.Split('-')[0]);
            }
            else
            {
                reqId = int.Parse(caRequestID);
            }

            Logger.Trace($"Get Single Certificate from Sectigo sslId:{reqId} | Req ID:{caRequestID}");
            var singleCert = Task.Run(async () => await Client.GetCertificate(reqId)).Result;
            Logger.Trace($"Found {singleCert.CommonName} from Sectigo. Pick up cert");

            var certData = PickupSingleCert(reqId, singleCert.CommonName);
            if (certData != null)
            {
                Logger.Trace($"Certificate Data Downloaded: {certData.Subject} ");

                Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
                return new CAConnectorCertificate()
                {
                    CARequestID = caRequestID,
                    Certificate = Convert.ToBase64String(certData.GetRawCertData()),
                    ProductID = singleCert.CertType.id.ToString(),
                    SubmissionDate = singleCert.requested,
                    ResolutionDate = singleCert.approved,
                    Status = ConvertToKeyfactorStatus(singleCert.status),
                    RevocationReason = ConvertToKeyfactorStatus(singleCert.status) == 21 ? 0 : 0xffffff,
                    RevocationDate = singleCert.revoked
                };

            }

            throw new Exception("Failed to download certificate data from Sectigo.");
        }


        /// <summary>
        /// Method to execture a new, renew, or reissue enrollment request from Keyfactor Command
        /// </summary>
        /// <param name="certificateDataReader">Interface to access existing certificate data from the Gateway database</param>
        /// <param name="csr">A base64 endocded certificate signing request in </param>
        /// <param name="subject">The distingused name of the certificate being requested</param>
        /// <param name="san">All supported (dns, ipv4, ipv6, ) san entries defined during enrollment</param>
        /// <param name="productInfo">Template details parsed from the Template configuration section</param>
        /// <param name="requestFormat"></param>
        /// <param name="enrollmentType"></param>
        /// <returns></returns>
        public override EnrollmentResult Enroll(ICertificateDataReader certificateDataReader, string csr, string subject, Dictionary<string, string[]> san, EnrollmentProductInfo productInfo, PKIConstants.X509.RequestFormat requestFormat, RequestUtilities.EnrollmentType enrollmentType)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            Logger.Info($"Begin {enrollmentType} enrollment for {subject}");
            try
            {
                bool multiDomain = bool.Parse(productInfo.ProductParameters["MultiDomain"]);
                Logger.Debug("Parse Subject for Common Name, Organization, and Org Unit");
                string commonName = ParseSubject(subject, "CN=");
                Logger.Trace($"Common Name: {commonName}");
                string orgStr = ParseSubject(subject, "O=");
                Logger.Trace($"Organization: {orgStr}");
                string ouStr = ParseSubject(subject, "OU=");
                Logger.Trace($"Org Unit: {ouStr}");

                var fieldList = Task.Run(async () => await Client.ListCustomFields()).Result;
                var mandatoryFields = fieldList.CustomFields?.Where(f => f.mandatory);

                Logger.Debug("Check for mandatory custom fields");
                foreach (CustomField reqField in mandatoryFields)
                {
                    Logger.Trace($"Checking product parameters for {reqField.name}");
                    if (!productInfo.ProductParameters.ContainsKey(reqField.name))
                    {
                        Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
                        return new EnrollmentResult { Status = 30, StatusMessage = $"Template {productInfo.ProductID} or Enrollment Fields do not contain a mandatory custom field value for of {reqField.name}" };
                    }
                }
                Logger.Debug($"Search for Organization by Name {orgStr}");

                int requstOrgId = 0;
                var org = Task.Run(async () => await GetOrganizationAsync(orgStr)).Result;
                if (org == null)
                {
                    throw new Exception($"Unable to find Organization by Name {orgStr} ");
                }

                Logger.Trace($"Found {org.id} for {orgStr}");

                Department ou = null;
                if (org.certTypes.Count == 0)
                {
                    Logger.Trace($"{orgStr} doses not contain a valid certificate type configuration. Check org unit");
                    ou = org.departments.Where(x => x.name.ToLower().Equals(ouStr.ToLower())).FirstOrDefault();

                    if (ou == null)
                    {
                        throw new Exception($"{ouStr} does not exist in the Sectigo Configuration. Please verify configuration");
                    }

                    requstOrgId = ou.id;
                }
                else
                {
                    requstOrgId = org.id;
                }


                //Check if SAN matches the SUBJECT CN when multidomain = false (single domain cert). 
                //If true, we need to send empty san array. if different, join array (remove if one?)
                string sanList = ParseSanList(san, multiDomain, commonName);
                
                var enrollmentProfile = Task.Run(async () => await GetProfile(int.Parse(productInfo.ProductID))).Result;
                if (enrollmentProfile != null)
                {
                    Logger.Trace($"Found {enrollmentProfile.name} profile for enroll request");
                }


                int sslId;
                string priorSn = string.Empty;

                switch (enrollmentType)
                {
                    case RequestUtilities.EnrollmentType.New:
                    case RequestUtilities.EnrollmentType.Reissue:
                    case RequestUtilities.EnrollmentType.Renew:

                        EnrollRequest request = new EnrollRequest
                        {
                            csr = csr,
                            orgId = requstOrgId,
                            term = Task.Run(async () => await GetProfileTerm(int.Parse(productInfo.ProductID))).Result,
                            certType = enrollmentProfile.id,
                            //External requestor is expected to be an email. Use config to pull the enrollment field or send blank
                            //sectigo will default to the account (API account) making the request. 
                            externalRequester = GetExternalRequestor(productInfo),
                            numberServers = 1,
                            serverType = -1,
                            subjAltNames = sanList,//,
                            comments = $"CERTIFICATE_REQUESTOR: {productInfo.ProductParameters["Keyfactor-Requester"]}"//this is how the current gateway passes this data
                        };

                        Logger.Debug($"Submit {enrollmentType} request for {subject}");
                        sslId = Task.Run(async () => await Client.Enroll(request)).Result;
                        break;

                    default:
                        return new EnrollmentResult { Status = 30, StatusMessage = $"Unsupported enrollment type {RequestUtilities.EnrollmentType.Reissue}" };
                }

                return PickUpEnrolledCertificate(sslId, subject);
            }
            catch (HttpRequestException httpEx)
            {
                Logger.Error($"Enrollment Failed due to a HTTP error: {httpEx.Message}");
                return new EnrollmentResult { Status = 30, StatusMessage = httpEx.Message };

            }
            catch (SectigoApiException apiEx)
            {
                Logger.Error($"Enrollment Failed due to an API error: {apiEx.Message}");
                return new EnrollmentResult { Status = 30, StatusMessage = apiEx.Message };
            }
            catch (Exception ex)
            {
                Logger.Error($"Enrollment Failed with the following error: {ex.Message}");
                Logger.Error($"Inner Exception Message: {ex.InnerException.Message}");
                return new EnrollmentResult { Status = 30, StatusMessage = ex.Message };
            }
        }

        private string GetExternalRequestor(EnrollmentProductInfo productInfo)
        {
            if (!String.IsNullOrEmpty(Config.ExternalRequestorFieldName))
            {
                if (!String.IsNullOrEmpty(productInfo.ProductParameters[Config.ExternalRequestorFieldName]))
                {
                    return productInfo.ProductParameters[Config.ExternalRequestorFieldName];
                }
            }
            return string.Empty;
        }
        public X509Certificate2 PickupSingleCert(int sslId, string subject)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            int retryCounter = 0;
            Thread.Sleep(2 * 1000);//small static delay as an attempt to avoid retries all together
            while (retryCounter < Config.PickupRetries)
            {
                Logger.Info($"Try number {retryCounter + 1} to pickup single certificate");
                var certificate = Task.Run(async () => await Client.PickupCertificate(sslId, subject)).Result;
                if (certificate != null && !String.IsNullOrEmpty(certificate.Subject))
                {
                    Logger.Info($"Successfully picked up certificate { certificate.Subject}");
                    Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
                    return certificate;
                }
                Thread.Sleep(Config.PickupDelayInSeconds * 1000);//convert seconds to ms for delay. 
                retryCounter++;
            }

            Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
            return null;
        }
        public EnrollmentResult PickUpEnrolledCertificate(int sslId, string subject)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            int retryCounter = 0;
            Thread.Sleep(2 * 1000);//small static delay as an attempt to avoid retries all together
            while (retryCounter < Config.PickupRetries)
            {
                Logger.Info($"Try number {retryCounter + 1} to pickup enrolled certificate");
                var certificate = Task.Run(async () => await Client.PickupCertificate(sslId, subject)).Result;
                if (certificate != null && !String.IsNullOrEmpty(certificate.Subject))
                {
                    Logger.Info($"Successfully enrolled for certificate { certificate.Subject}");
                    Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
                    return new EnrollmentResult
                    {
                        CARequestID = $"{sslId}",
                        Certificate = Convert.ToBase64String(certificate.GetRawCertData()),
                        Status = 20,
                        StatusMessage = $"Successfully enrolled for certificate {certificate.Subject}"
                    };
                }
                Thread.Sleep(Config.PickupDelayInSeconds * 1000);//convert seconds to ms for delay. 
                retryCounter++;
            }

            Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
            return new EnrollmentResult
            {
                Status = 13,//should this be pending or failed.  I think pending since we should pick up the cert in a later sync
                StatusMessage = "Failed to pickup certificate. Check SCM portal to determine if addtional approval is required"
            };
        }

        /// <summary>
        /// Method that sets up configuration class and API client. Called before each sync, revocation, or enrollment.  <see cref="ICAConnectorConfigProvider"/>
        ///  is the configuration that is currently saved in the AnyGateway database for the CA.
        /// </summary>
        /// <param name="configProvider"></param>
        public override void Initialize(ICAConnectorConfigProvider configProvider)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);

            Config = JsonConvert.DeserializeObject<SectigoCAConfig>(JsonConvert.SerializeObject(configProvider.CAConnectionData));

            Client = InitializeRestClient(configProvider.CAConnectionData, Logger);

            Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
        }

        /// <summary>
        /// Method to revoke a certificate from the Sectigo CA
        /// </summary>
        /// <param name="caRequestID"></param>
        /// <param name="hexSerialNumber"></param>
        /// <param name="revocationReason"></param>
        /// <returns></returns>
        public override int Revoke(string caRequestID, string hexSerialNumber, uint revocationReason)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);

            var response = Task.Run(async () => await Client.RevokeSslCertificateById(int.Parse(caRequestID), RevokeReasonToString(revocationReason))).Result;

            Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
            if (response)//will throw an exception if false
            {
                return 21;//revoked
            }

            return -1;
        }
        /// <summary>
        /// Method to respond to certutil -ping command
        /// </summary>
        public override void Ping()
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
        }

        /// <summary>
        /// Method to validate CAConnection section of configuration during import of the configuration JSON file
        /// </summary>
        /// <param name="connectionInfo"></param>
        public override void ValidateCAConnectionInfo(Dictionary<string, object> connectionInfo)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            //determine required fields
            //URL
            //Auth Type (Cert or UN PASSWORD)
            List<string> errors = new List<string>();
            errors.Add(ValidateConfigurationKey(connectionInfo, Constants.API_ENDPOINT_KEY));
            errors.Add(ValidateConfigurationKey(connectionInfo, Constants.AUTH_TYPE_KEY));

            Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
            if (errors.Any(s => !String.IsNullOrEmpty(s)))
            {
                throw new Exception(String.Join("|", errors.All(s => !String.IsNullOrEmpty(s))));
            }
        }

        /// <summary>
        /// Method to validate product details of a template configurd in the configuration JSON file
        /// </summary>
        /// <param name="productInfo"></param>
        /// <param name="connectionInfo"></param>

        public override void ValidateProductInfo(EnrollmentProductInfo productInfo, Dictionary<string, object> connectionInfo)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            SectigoApiClient localClient = InitializeRestClient(connectionInfo, Logger);

            var profileList = Task.Run(async () => await localClient.ListSslProfiles()).Result;
            if (profileList.SslProfiles.Where(p => p.id == int.Parse(productInfo.ProductID)).Count() == 0)
            {
                Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
                throw new Exception($"Unable to find SSl Profile with ID {productInfo.ProductID}");
            }
            Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);

        }

        private async Task<int> GetOrganizationId(string orgName)
        {
            var orgList = await Client.ListOrganizations();
            return orgList.Organizations.Where(x => x.name.ToLower().Equals(orgName.ToLower())).FirstOrDefault().id;
        }
        private async Task<Organization> GetOrganizationAsync(string orgName)
        {
            var orgList = await Client.ListOrganizations();
            return orgList.Organizations.Where(x => x.name.ToLower().Equals(orgName.ToLower())).FirstOrDefault();
        }

        private async Task<int> GetProfileTerm(int profileId)
        {
            var profileList = await Client.ListSslProfiles();
            return profileList.SslProfiles.Where(x => x.id == profileId).FirstOrDefault().terms[0];
        }

        private async Task<Profile> GetProfile(int profileId)
        {
            var profileList = await Client.ListSslProfiles();
            return profileList.SslProfiles.Where(x => x.id == profileId).FirstOrDefault();
        }


        #region Static Helpers
        private static string ParseSanList(Dictionary<string, string[]> san, bool multiDomain, string commonName)
        {
            
            string sanList = string.Empty;
            if (san.ContainsKey("dns"))
            {
                string[] requestSanList = san["dns"];
                if (!multiDomain)
                {
                    if (requestSanList.Contains(commonName) && requestSanList.Count() > 1)
                    {
                        List<string> sans = requestSanList.ToList();
                        sans.Remove(commonName);
                        sanList = string.Join(",", sans.ToArray());
                    }
                }
                else
                {
                    if (requestSanList.Contains(commonName))
                    {
                        List<string> sans = requestSanList.ToList();
                        sans.Remove(commonName);
                        sanList = string.Join(",", sans.ToArray());
                    }
                    else
                    {
                        List<string> sans = requestSanList.ToList();
                        sanList = string.Join(",", sans.ToArray());
                    }

                }

            }

            return sanList;
        }

        private static string ParseSubject(string subject, string rdn)
        {
            string escapedSubject = subject.Replace("\\,", "|");
            string rdnString = escapedSubject.Split(',').ToList().Where(x => x.Contains(rdn)).FirstOrDefault();

            if (!string.IsNullOrEmpty(rdnString))
            {
                return rdnString.Replace(rdn, "").Replace("|", ",").Trim();
            }
            else
            {
                
                throw new Exception($"The request is missing a {rdn} value");
            }
        }
        /// <summary>
        /// Ensure the key is configured in the CAConnectionDetail section
        /// </summary>
        /// <param name="connectionInfo"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        private static string ValidateConfigurationKey(Dictionary<string, object> connectionInfo, string key)
        {
            if (!connectionInfo.TryGetValue(key, out object tempValue) && tempValue != null)
            {
                return $"{key} is a required configuration value";
            }

            return string.Empty;
        }
        private static int ConvertToKeyfactorStatus(string status)
        {
            switch (status.ToUpper())
            {
                case "ISSUED":
                case "ENROLLED - PENDING DOWNLOAD":
                case "APPROVED":
                case "APPLIED":
                case "DOWNLOADED":
                    return 20;
                case "REQUESTED":
                case "AWAITING APPROVAL":
                case "NOT ENROLLED":
                    return 13;
                case "REVOKED":
                case "EXPIRED":
                    return 21;
                case "ANY":
                default:
                    return -1;//unknown
            }

        }

        public static string RevokeReasonToString(UInt32 revokeType)
        {
            switch (revokeType)
            {
                case 1:
                    return "Compromised Key";
                case 2:
                    return "CA Compromised";
                case 3:
                    return "Affiliation Changed";
                case 4:
                    return "Superseded";
                case 5:
                    return "Cessation of Operation";
                case 6:
                    return "Certificate Hold";
                default:
                    return "Unspecified";
            }
        }

        public static int RevokeStringToCode(string revokePhrase)
        {
            switch (revokePhrase.ToLower())
            {
                case "compromised key":
                    return 1;
                case "ca compromised":
                    return 2;
                case "affiliation changed":
                    return 3;
                case "superseded":
                    return 4;
                case "cessation of operation":
                    return 5;
                case "certificate hold":
                    return 6;
                default:
                    return 0;
            }
        }
        private static SectigoApiClient InitializeRestClient(Dictionary<string, object> connectionInfo, ILog logger)
        {
            logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            SectigoCAConfig localConfig = JsonConvert.DeserializeObject<SectigoCAConfig>(JsonConvert.SerializeObject(connectionInfo));
            WebRequestHandler webRequestHandler = new WebRequestHandler();

            if (localConfig.AuthenticationType.ToLower() == "certificate")
            {
                webRequestHandler.ClientCertificateOptions = ClientCertificateOption.Manual;
                X509Certificate2 authCert = GetClientCertificate(connectionInfo, logger);
                if (authCert == null)
                {
                    logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
                    throw new Exception("AuthType set to Certificate, but no certificate found!");
                }

                webRequestHandler.ClientCertificates.Add(authCert);
            }

            HttpClient restClient = new HttpClient(webRequestHandler)
            {
                BaseAddress = new Uri(localConfig.ApiEndpoint)
            };

            restClient.DefaultRequestHeaders.Add(Constants.CUSTOMER_URI_KEY, localConfig.CustomerUri);
            restClient.DefaultRequestHeaders.Add(Constants.CUSTOMER_LOGIN_KEY, localConfig.Username);
            //Determine

            if (localConfig.AuthenticationType.ToLower() == "password")
            {
                restClient.DefaultRequestHeaders.Add(Constants.CUSTOMER_PASSWORD_KEY, localConfig.Password);
            }

            logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
            return new SectigoApiClient(restClient);
        }
        private static X509Certificate2 GetClientCertificate(Dictionary<string, object> config, ILog logger)
        {
            logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            Dictionary<string, object> caConnectionCertificateDetail = config["ClientCertificate"] as Dictionary<string, object>;

            StoreName sn;
            StoreLocation sl;
            string thumbprint = (string)caConnectionCertificateDetail["Thumbprint"];

            if (String.IsNullOrEmpty(thumbprint) ||
                !Enum.TryParse((string)caConnectionCertificateDetail["StoreName"], out sn) ||
                !Enum.TryParse((string)caConnectionCertificateDetail["StoreLocation"], out sl))
            {
                throw new Exception("Unable to find client authentication certificate");
            }

            X509Certificate2Collection foundCerts;
            using (X509Store currentStore = new X509Store(sn, sl))
            {
                logger.Trace($"Search for client auth certificates with Thumprint {thumbprint} in the {sn}{sl} certificate store");

                currentStore.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foundCerts = currentStore.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, true);
                logger.Trace($"Found {foundCerts.Count} certificates in the {currentStore.Name} store");
                currentStore.Close();
            }
            if (foundCerts.Count > 1)
            {
                throw new Exception($"Multiple certificates with Thumprint {thumbprint} found in the {sn}{sl} certificate store");
            }
            logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
            if (foundCerts.Count > 0)
                return foundCerts[0];

            return null;
        }
        #endregion

        #region Obsolete Methods
        [Obsolete]
        public override EnrollmentResult Enroll(string csr, string subject, Dictionary<string, string[]> san, EnrollmentProductInfo productInfo, CSS.PKI.PKIConstants.X509.RequestFormat requestFormat, RequestUtilities.EnrollmentType enrollmentType)
        {
            throw new NotImplementedException();
        }
        [Obsolete]
        public override void Synchronize(ICertificateDataReader certificateDataReader, BlockingCollection<CertificateRecord> blockingBuffer, CertificateAuthoritySyncInfo certificateAuthoritySyncInfo, CancellationToken cancelToken, string logicalName)
        {
            throw new NotImplementedException();
        }
        #endregion  
    }
}
