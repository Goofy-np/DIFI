using Difi.IDPorten.Integration.Dynamic365.Misc;
using Difi.IDPorten.Integration.Dynamic365.Models;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;

namespace Difi.IDPorten.Integration.Dynamic365
{
    /// <summary>
    /// root class that connects to Dynamic365 WEBAPI for operations
    /// </summary>
    public class CrmInstanceManager
    {

        #region Public Functions

        /// <summary>
        /// entry point funtion to delete all integrations and create new integrations
        /// </summary>
        /// <param name="integrations"></param>
        public static void ProcessIntegrations(List<IdPortenModel> integrations)
        {
            GetIntegrationsToProcess(integrations).Wait();

        }

        /// <summary>
        /// Connect to Crm and saves the records
        /// </summary>
        /// <returns></returns>
        static void SaveIntegrationRecords(List<IdPortenModel> integrations, string systemuserID)
        {

            ConnectAndSaveIntegration(integrations, systemuserID).Wait();

        }


        /// <summary>
        /// logs the error to log entity
        /// </summary>
        /// <param name="functioname"></param>
        /// <param name="error"></param>
        public static void LogError(string functioname, string error)
        {

            _logerror(functioname, error).Wait();

        }

        #endregion


        #region Private Helper Function


        /// <summary>
        /// Connect to Crm and saves the records
        /// </summary>
        /// <returns></returns>
        /// 
        static async Task ConnectAndSaveIntegration(List<IdPortenModel> integrations, string systemuserID)
        {
            string _recordId = string.Empty; string _error = string.Empty;
            try
            {
                if (ConfigurationManager.AppSettings["clientId"] != null && ConfigurationManager.AppSettings["secretkey"] != null)
                {
                    if (ConfigurationManager.AppSettings["ApiUrl"] != null)
                    {
                        string clientId = Convert.ToString(ConfigurationManager.AppSettings["clientId"]);
                        string secretkey = Convert.ToString(ConfigurationManager.AppSettings["secretkey"]);
                        string apiUrl = Convert.ToString(ConfigurationManager.AppSettings["ApiUrl"]);
                        DateTime _expiresOn = DateTime.UtcNow; string token = string.Empty;

                        AuthenticationParameters ap = AuthenticationParameters.CreateFromResourceUrlAsync(new Uri(apiUrl)).Result;
                        ClientCredential cred = new ClientCredential(clientId, secretkey);
                        AuthenticationContext ctx = new AuthenticationContext(ap.Authority);
                        AuthenticationResult result = ctx.AcquireTokenAsync(ap.Resource, cred).Result;

                        if (result != null && (!string.IsNullOrWhiteSpace(result.AccessToken)))
                        {
                            _expiresOn = getTokenExpiry(result.AccessToken);// get token life time
                            token = result.AccessToken; // for next token if expiers                                                       

                            for (int k = 0; k < integrations.Count; k++)
                            {
                                IdPortenModel _int = copyObject(integrations[k]);
                                if (!string.IsNullOrWhiteSpace(_int.Customer_Organization_Number))
                                {
                                    _recordId = string.Format("ID: {0}", _int.ID); // for main exception
                                    if (DateTimeOffset.UtcNow < _expiresOn) // not expires
                                    {
                                        await ProcessIntegrationRecord(_int, token, apiUrl, systemuserID);
                                    }
                                    else // get new token if expire, and  process this record only (in case longer transactions)
                                    {
                                        TokenModel _newtoken = getNewToken();
                                        if (_newtoken != null)
                                        {
                                            token = _newtoken.Token;
                                            _expiresOn = getTokenExpiry(_newtoken.Token);// get token life time
                                            await ProcessIntegrationRecord(_int, token, apiUrl, systemuserID);
                                        }
                                        else
                                        {
                                            _error = string.Format("{0} , Error:  Reason: error getting new token", _recordId);
                                            LogError("ConnectAndSaveIntegration()", _error);
                                            Console.WriteLine("ConnectAndSaveIntegration()", _error);
                                        }
                                    }

                                } //if NULL

                            }// for each
                        }
                    }
                }

                Console.WriteLine(" ===== finsh saving records. =====");
            }
            catch (Exception ex)
            {
                _error = string.Format("{0} ,Error: {1}", _recordId, ex.GetBaseException().ToString());
                LogError("Main.Exception.ConnectAndSaveIntegration()", _error);
                Console.WriteLine("Main.Exception.ConnectAndSaveIntegration()", _error);

            }
        }

        /// <summary>
        /// Creates new integration records
        /// (only those having valid customer organization number)
        /// </summary>
        /// <param name="_int"></param>
        /// <param name="token"></param>
        /// <param name="apiUrl"></param>
        /// <returns></returns>
        static async Task ProcessIntegrationRecord(IdPortenModel _int, string token, string apiUrl, string systemuserID)
        {
            string _error = string.Empty;
            try
            {
                RootObject retrievedAccount = null;
                RootObject retrievedAccountsupplier = null; string _Id = string.Empty;

                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = new TimeSpan(0, 2, 0);
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    client.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
                    client.DefaultRequestHeaders.Add("OData-Version", "4.0");
                    client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    // get the releveant record if neeeded
                    if (!string.IsNullOrWhiteSpace(_int.Customer_Organization_Number))
                        retrievedAccount = await getOrganization(_int.Customer_Organization_Number, apiUrl, client);

                    if (!string.IsNullOrWhiteSpace(_int.Supplier_Organization_Number))
                        retrievedAccountsupplier = await getOrganization(_int.Supplier_Organization_Number, apiUrl, client);

                    if ((retrievedAccount != null && retrievedAccount.value != null) && (retrievedAccount.value.Count > 0))
                    {
                        _Id = (string.Format("{0}_{1}", _int.ID, _int.Integration_Type)); //build ID to find integrtion
                        Integration_Json_Model retrievedintegration = await GetIntegrationByIdNameUser(_Id, systemuserID, client);

                        if ((retrievedintegration != null && retrievedintegration.value != null) && (retrievedintegration.value.Count <= 0))// send for save                                            
                            CreateIntegration(retrievedAccount, retrievedAccountsupplier, client, _int, apiUrl, systemuserID);
                        else if ((retrievedintegration != null && retrievedintegration.value != null) && (retrievedintegration.value.Count > 0)) // update if needed                                            
                            UpdateIntegration(retrievedAccount, retrievedAccountsupplier, client, _int, apiUrl, retrievedintegration.value[0], _Id);
                    }
                    else // traps invalid customer organization, NO NEED
                    {
                        /*if (!string.IsNullOrWhiteSpace(_int.Customer_Organization_Number))
                        {
                            string info = string.Format("ID: {0} , Customer_Organization_Number: {1}", _int.ID, _int.Customer_Organization_Number);
                            LogError("Invalid organization number (Not in CRM)", info);
                        }*/
                    }


                }//using

            }
            catch (Exception ex)
            {
                _error = string.Format("ID: {0} , Error: {1}", _int.ID, ex.GetBaseException().ToString());
                LogError("ProcessIntegrationRecord()", _error);
                Console.WriteLine("ProcessIntegrationRecord()", _error);
            }

        }


        /// <summary>
        /// create the integration recod
        /// </summary>
        /// <param name="retrievedAccount">reterieved from Organization number</param>
        /// <param name="retrievedAccountsupplier">reterieved from supplier number</param>
        /// <param name="client">httpclient to call CRMWebAPI</param>
        /// <param name="_int">current WebAPI record</param>
        /// <param name="apiUrl">WebAPi url</param>
        static void CreateIntegration(RootObject retrievedAccount, RootObject retrievedAccountsupplier, HttpClient client, IdPortenModel _int, string apiUrl, string systemuserID)
        {
            string _error = string.Empty;
            try
            {
                string _IdInt = string.Empty; CultureInfo culture = CultureInfo.CreateSpecificCulture(CultureInfo.CurrentCulture.ToString());
                DateTimeStyles styles = DateTimeStyles.AssumeLocal; string content = String.Empty;
                DateTime _validDate = DateTime.Now; DateTime _expDate = DateTime.Now; HttpRequestMessage request = null;
                HttpResponseMessage response = null; string _IntegrationId = string.Empty;
                IEnumerable<string> values;

                if ((retrievedAccount != null) && (retrievedAccount.value != null) && (retrievedAccount.value.Count > 0))
                {
                    if ((!string.IsNullOrWhiteSpace(retrievedAccount.value[0].name)))// if there is org number
                        _IdInt = string.Format("{0} - {1}", _int.ID, retrievedAccount.value[0].name);
                }
                else
                    _IdInt = string.Format("{0}", _int.ID);

                JObject _integrationModel = new JObject();
                _integrationModel.Add("crayon_tenesteprofil_id", TruncateLongString(_int.ID + "_" + _int.Integration_Type, 449)); //400 max length



                _integrationModel.Add("cap_beskrivelse", _int.Description);

                if (!string.IsNullOrWhiteSpace(_int.Integration_Type))
                {
                    _integrationModel.Add("cap_integration_type", Convert.ToInt32((getIntegrationTypeCode(_int.Integration_Type))));
                    _integrationModel.Add("cap_integration_type_name", _int.Integration_Type.Replace("_", " "));
                    _integrationModel.Add("cap_integration_type2", Convert.ToInt32((getIntegrationTypeForSammerArbeidPorten(_int.Integration_Type))));
                    _integrationModel.Add("cap_idintegrasjon", _int.ID);//JB added this for the new field ID Integrasjon

                }

                _integrationModel.Add("crayon_name", _int.Integration_Type.Replace("_", " "));// 400 max length
                _integrationModel.Add("cap_integration_id_title", _IdInt);
                _integrationModel.Add("crayon_tenesteprofil_produksjonssatt", DateTime.Now);//must

                if ((retrievedAccountsupplier != null) && retrievedAccountsupplier.value.Count > 0)
                    _integrationModel.Add("cap_LeverandrId@odata.bind", "/accounts(" + retrievedAccountsupplier.value[0].accountid + ")");


                if (!string.IsNullOrEmpty(_int.Created))
                {
                    _validDate = DateTime.Parse(_int.Created, culture, styles);
                    _integrationModel.Add("cap_createdondate", _validDate);
                }
                else
                    _integrationModel.Add("cap_createdondate", DateTime.Now);//default

                content = JsonConvert.SerializeObject(_integrationModel, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
                request = new HttpRequestMessage(HttpMethod.Post, apiUrl + "crayon_tenesteprofils");
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                response = client.SendAsync(request).Result;
                if (response.Content != null)
                {
                    if ((response.StatusCode == HttpStatusCode.NoContent) || (response.StatusCode == HttpStatusCode.OK)) { }
                    else
                    {
                        Exception ex = new CrmHttpResponseException(response.Content);
                        _error = string.Format("Id: {0} , Error: {1} ", _int.ID, ex.GetBaseException().ToString());
                        LogError("CreateIntegration(). Failed to create integration record.", _error);
                        Console.WriteLine("CreateIntegration(). Failed to create integration record.", _error);
                    }
                }

            }
            catch (Exception ex)
            {
                _error = string.Format("Id: {0} , Error: {1} ", _int.ID, ex.GetBaseException().ToString());
                LogError("Main.CreateIntegration()", _error);
                Console.WriteLine("Main.CreateIntegration()", _error);
            }
        }


        /// <summary>
        /// Save N:N relationship between Integration and Account/Levronder
        /// </summary>
        /// <param name="Id">Guid of integraiton</param>
        /// <param name="apiUrl"></param>
        /// <param name="client"></param>
        /// <param name="CustomerOrLevronderGuid">Guid of Account</param>
        static void saveCustomerLeveronder(string Id, string apiUrl, HttpClient client, string CustomerOrLevronderGuid)
        {
            string _error = string.Empty;
            try
            {
                JObject _integrationModel = new JObject();
                _integrationModel.Add("@odata.id", apiUrl + "accounts(" + CustomerOrLevronderGuid + ")");

                string content = JsonConvert.SerializeObject(_integrationModel, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, apiUrl + "crayon_tenesteprofils(" + Id + ")/cap_tenesteprofil_account_NN/$ref");
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");

                HttpResponseMessage response = client.SendAsync(request).Result;
                if (response.Content != null)
                {
                    if (response.StatusCode == HttpStatusCode.NoContent)
                    {

                    }
                    else
                    {
                        Exception ex = new CrmHttpResponseException(response.Content);
                        _error = string.Format("Account Id: {0} , Error: {1} ", CustomerOrLevronderGuid, ex.GetBaseException().ToString());
                        //LogError("CrmHttpResponseException.saveCustomerLeveronder(). Failed to asociate account/levronder." + _error, ex.GetBaseException().ToString()); //}
                        Console.WriteLine("CrmHttpResponseException.saveCustomerLeveronder(). Failed to asociate account/levronder." + _error, ex.GetBaseException().ToString());
                    }

                }

            }
            catch (Exception ex)
            {
                _error = string.Format("Id: {0} , Error: {1} ", Id, ex.GetBaseException().ToString());
                LogError("Main.saveCustomerLeveronder()", "Unable to asociate Customer or Leveronder. ID  " + _error + ex.GetBaseException().ToString());
                Console.WriteLine("Main.saveCustomerLeveronder()", "Unable to asociate Customer or Leveronder. " + _error, ex.GetBaseException().ToString());
            }

        }



        static void removeCustomerLeveronderAddNewRefrence(string Id, string apiUrl, HttpClient client, string CustomerOrLevronderGuid, string newCustomerOrLevronderGuid)
        {
            string _error = string.Empty;
            try
            {
                if (!string.IsNullOrWhiteSpace(CustomerOrLevronderGuid))
                {
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, apiUrl + "crayon_tenesteprofils(" + Id + ")/cap_tenesteprofil_account_NN/$ref?$id=" + apiUrl + "accounts(" + CustomerOrLevronderGuid + ")");
                    HttpResponseMessage response = client.SendAsync(request).Result;
                    if (response.Content != null)
                    {
                        if (response.StatusCode == HttpStatusCode.NoContent)
                        {
                            // add new one
                            saveCustomerLeveronder(Id, apiUrl, client, newCustomerOrLevronderGuid);
                        }
                        else
                        {
                            Exception ex = new CrmHttpResponseException(response.Content);
                            _error = string.Format("Account Id: {0} , Error: {1} ", CustomerOrLevronderGuid, ex.GetBaseException().ToString());
                            LogError("CrmHttpResponseException.removeCustomerLeveronderREfrence(). Failed to diassociate account/levronder." + _error, ex.GetBaseException().ToString()); //}
                            Console.WriteLine("CrmHttpResponseException.removeCustomerLeveronderREfrence(). Failed to diassociate account/levronder." + _error, ex.GetBaseException().ToString());
                        }

                    }
                }
                else
                {
                    saveCustomerLeveronder(Id, apiUrl, client, newCustomerOrLevronderGuid);
                }

            }
            catch (Exception ex)
            {
                _error = string.Format("Id: {0} , Error: {1} ", Id, ex.GetBaseException().ToString());
                LogError("Main.removeCustomerLeveronderREfrence()", "Unable to diassociate Customer or Leveronder. ID  " + _error + ex.GetBaseException().ToString());
                Console.WriteLine("Main.removeCustomerLeveronderREfrence()", "Unable to diassociate Customer or Leveronder. " + _error, ex.GetBaseException().ToString());
            }

        }




        /// <summary>
        /// Get the accoutn by organization number
        /// </summary>
        /// <param name="orgNumber"></param>
        /// <param name="token"></param>
        /// <param name="apiUrl"></param>
        /// <returns></returns>
        static async Task<RootObject> getOrganization(string orgNumber, string apiUrl, HttpClient client)
        {
            RootObject retrievedAccount = null; string _error = string.Empty;
            try
            {

                HttpResponseMessage response = await client.GetAsync(apiUrl + "accounts?$select=accountid,name&$filter=crayon_organisasjonsnr eq '" + orgNumber + "'&$top=1");
                // crayon_tenesteprofils?$top=3                
                if (response.Content != null)
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        retrievedAccount = JsonConvert.DeserializeObject<RootObject>(
                           await response.Content.ReadAsStringAsync());
                    }
                    else
                    {
                        Exception ex = new CrmHttpResponseException(response.Content);
                        _error = string.Format("Id: {0} , Error: {1} ", orgNumber, ex.GetBaseException().ToString());
                        LogError("getOrganization(). Failed to read account by Orgnumber.", ex.GetBaseException().ToString()); //}
                        Console.WriteLine("getOrganization(). Failed to read account by Orgnumber.", ex.GetBaseException().ToString());
                    }
                }

            }
            catch (Exception ex)
            {
                _error = string.Format("Id: {0} , Error: {1} ", orgNumber, ex.GetBaseException().ToString());
                LogError("Main.getOrganization()", "Error in getting organization number. " + ex.GetBaseException().ToString());
                Console.WriteLine("Main.getOrganization()", "Error in getting organization number. " + ex.GetBaseException().ToString());
            }

            return retrievedAccount;
        }



        /// <summary>
        /// Get new token if expired
        /// </summary>
        /// <returns></returns>
        static TokenModel getNewToken()
        {
            TokenModel _tokenModel = null;
            try
            {
                if (ConfigurationManager.AppSettings["clientId"] != null && ConfigurationManager.AppSettings["secretkey"] != null)
                {
                    if (ConfigurationManager.AppSettings["ApiUrl"] != null)
                    {
                        _tokenModel = new TokenModel();
                        string clientId = Convert.ToString(ConfigurationManager.AppSettings["clientId"]);
                        string secretkey = Convert.ToString(ConfigurationManager.AppSettings["secretkey"]);
                        string apiUrl = Convert.ToString(ConfigurationManager.AppSettings["ApiUrl"]);
                        string oauthUserName = Convert.ToString(ConfigurationManager.AppSettings["OAuthUserName"]);
                        AuthenticationParameters ap = AuthenticationParameters.CreateFromResourceUrlAsync(new Uri(apiUrl)).Result;
                        ClientCredential cred = new ClientCredential(clientId, secretkey);
                        AuthenticationContext ctx = new AuthenticationContext(ap.Authority);
                        AuthenticationResult result = ctx.AcquireTokenAsync(ap.Resource, cred).Result;
                        if (result != null && (!string.IsNullOrWhiteSpace(result.AccessToken)))
                        {
                            _tokenModel.Token = result.AccessToken;
                            _tokenModel.ExpiryOffset = result.ExpiresOn;
                            _tokenModel.APIUrl = apiUrl;
                            _tokenModel.OAuthUserName = oauthUserName;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Main.getNewToken()", "Error in getting new token. " + ex.GetBaseException().ToString());
                Console.WriteLine("Main.getNewToken()", "FError in getting new token. ", ex.GetBaseException().ToString());

            }

            return _tokenModel;

        }

        /// <summary>
        /// Log rror to CRM entity(cap_idportenintegration_log)
        /// </summary>
        /// <param name="functioname"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        static async Task _logerror(string functioname, string error)
        {

            try
            {
                if (ConfigurationManager.AppSettings["clientId"] != null && ConfigurationManager.AppSettings["secretkey"] != null)
                {
                    if (ConfigurationManager.AppSettings["ApiUrl"] != null)
                    {
                        string clientId = Convert.ToString(ConfigurationManager.AppSettings["clientId"]);
                        string secretkey = Convert.ToString(ConfigurationManager.AppSettings["secretkey"]);
                        string apiUrl = Convert.ToString(ConfigurationManager.AppSettings["ApiUrl"]);
                        DateTime _expiresOn = DateTime.Now;//just
                        string token = string.Empty;
                        AuthenticationParameters ap = AuthenticationParameters.CreateFromResourceUrlAsync(new Uri(apiUrl)).Result;
                        ClientCredential cred = new ClientCredential(clientId, secretkey);
                        AuthenticationContext ctx = new AuthenticationContext(ap.Authority);
                        AuthenticationResult result = await ctx.AcquireTokenAsync(ap.Resource, cred);
                        if (result != null && (!string.IsNullOrWhiteSpace(result.AccessToken)))
                        {
                            _expiresOn = getTokenExpiry(result.AccessToken);// get token life time
                            token = result.AccessToken; // for next token if expiers
                            if (DateTime.UtcNow < _expiresOn)
                            {

                                if (!string.IsNullOrWhiteSpace(error))
                                {

                                    using (HttpClient client = new HttpClient())
                                    {
                                        client.Timeout = new TimeSpan(0, 2, 0);
                                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                                        client.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
                                        client.DefaultRequestHeaders.Add("OData-Version", "4.0");
                                        client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                                        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                                        ErrorLogModel _log = new ErrorLogModel();
                                        _log.cap_name = apiUrl;
                                        _log.cap_functionname = functioname;
                                        _log.cap_error_message = error;
                                        string content = String.Empty;
                                        content = JsonConvert.SerializeObject(_log, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
                                        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, apiUrl + "/cap_idportenintegration_logs");
                                        request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                                        HttpResponseMessage response = client.SendAsync(request).Result;

                                        if (!response.IsSuccessStatusCode)//failed to save
                                        {
                                            Exception ex = new CrmHttpResponseException(response.Content);
                                            string ermsg = string.Format("{0},{1},{2}", DateTime.Now.ToLongDateString(), "_logerror(): Error saving to integration log entity.", ex.GetBaseException().ToString());
                                            Console.WriteLine(ermsg);
                                        }

                                    }

                                }// error

                            } // _expiresOn
                            else
                            { // get new token
                                TokenModel _newtoken = getNewToken();
                                if (_newtoken != null)
                                {
                                    token = _newtoken.Token;
                                    _expiresOn = getTokenExpiry(_newtoken.Token);// get new token and try to save error again.
                                    if (DateTime.UtcNow < _expiresOn)
                                    {
                                        if (!string.IsNullOrWhiteSpace(error))
                                        {

                                            using (HttpClient client = new HttpClient())
                                            {
                                                client.Timeout = new TimeSpan(0, 2, 0);
                                                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                                                client.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
                                                client.DefaultRequestHeaders.Add("OData-Version", "4.0");
                                                client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                                                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                                                ErrorLogModel _log = new ErrorLogModel();
                                                _log.cap_name = DateTime.Now.ToLongDateString();
                                                _log.cap_functionname = functioname;
                                                _log.cap_error_message = error;
                                                string content = String.Empty;
                                                content = JsonConvert.SerializeObject(_log, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
                                                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, apiUrl + "/cap_idportenintegration_logs");
                                                request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                                                HttpResponseMessage response = client.SendAsync(request).Result;

                                                if (!response.IsSuccessStatusCode)//failed to save
                                                {
                                                    Exception ex = new CrmHttpResponseException(response.Content);
                                                    string ermsg = string.Format("{0},{1},{2}", DateTime.Now.ToLongDateString(), "else._logerror(): Error saving to integration log entity.", ex.GetBaseException().ToString());
                                                    Console.WriteLine(ermsg);
                                                }

                                            }

                                        }// error

                                    } // _expiresOn

                                }

                            }

                        }
                    }
                }


            }
            catch (Exception ex)
            {
                string ermsg = string.Format("{0},{1}", DateTime.Now.ToLongDateString(), "Main._logerror() " + ex.GetBaseException().ToString());
                Console.WriteLine(ermsg);
            }
        }

        /// <summary>
        /// Get token expiry time (JSON TOken)
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        static DateTime getTokenExpiry(string token)
        {

            DateTime _expiresOn = DateTime.Now;
            try
            {

                // Get just the JWT part of the token.
                string jwt = token
                    .Split(new Char[] { '.' })[1];

                // Undo the URL encoding.
                jwt = jwt.Replace('-', '+');
                jwt = jwt.Replace('_', '/');
                switch (jwt.Length % 4)
                {
                    case 0: break;
                    case 2: jwt += "=="; break;
                    case 3: jwt += "="; break;
                    default:
                        {
                            string ermsg = string.Format("{0},{1},{2}", DateTime.Now.ToLongDateString(), "Reason: The jwt token (base64url) string is not valid.", "");

                            throw new System.Exception(
                       "The jwt token (base64url) string is not valid (getTokenExpiry()).");
                        }
                }

                // Decode the bytes from base64 and write to a JSON string.
                var bytes = Convert.FromBase64String(jwt);
                string jsonString = UTF8Encoding.UTF8.GetString(bytes, 0, bytes.Length);

                JWT_Token_Model _jwtToken = JsonConvert.DeserializeObject<JWT_Token_Model>(jsonString);
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                _expiresOn = epoch.AddSeconds(Convert.ToDouble(_jwtToken.exp));

            }
            catch (Exception ex)
            {

                string ermsg = string.Format("{0},{1},{2}", DateTime.Now.ToLongDateString(), "Reason: Error getting token expiry (getTokenExpiry()).", ex.GetBaseException().ToString());
                Console.WriteLine(ermsg);
            }

            return _expiresOn;

        }

        /// <summary>
        /// Get Integration TypeCode by Name
        /// cap_integration_type
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static string getIntegrationTypeCode(string type)
        {
            string _code = "809020000";//DEFAULT
            try
            {
                switch (type.ToUpper())
                {
                    case "IDPORTEN_SAML2":
                        _code = "809020000";
                        break;
                    case "IDPORTEN_SAML2_ONBEHALF":
                        _code = "809020001";
                        break;
                    case "IDPORTEN_OIDC":
                        _code = "809020002";
                        break;
                    case "IDPORTEN_OIDC_ONBEHALFOF":
                        _code = "809020003";
                        break;
                    case "KRR_SOAP":
                        _code = "809020004";
                        break;
                    case "KRR_SOAP_ONBEHALFOF":
                        _code = "809020005";
                        break;
                    case "KRR_REST":
                        _code = "809020006";
                        break;
                    case "KRR_REST_ONBEHALFOF":
                        _code = "809020007";
                        break;

                    case "Digital postkasse":
                        _code = "809020008";
                        break;

                    case "eFormidling":
                        _code = "809020009";
                        break;
                    case "eInnsyn":
                        _code = "809020010";
                        break;

                    case "Signeringstjenesten":
                        _code = "809020011";
                        break;
                    case "EFORMIDLING_DPI":
                        _code = "809020012";
                        break;
                    case "EFORMIDLING_DPO":
                        _code = "809020013";
                        break;
                    case "EFORMIDLING_DPV":
                        _code = "809020014";
                        break;
                    case "EFORMIDLING_DPF":
                        _code = "809020015";
                        break;
                    case "EFORMIDLING_DPE":
                        _code = "809020016";
                        break;

                    default:
                        _code = "809020000";//nothing
                        break;



                }
            }
            catch (Exception ex)
            {

                LogError("getIntegrationTypeCode()", "Reason: error converting integration type. " + ex.GetBaseException().ToString());
                Console.WriteLine("getIntegrationTypeCode()", "Reason: error converting integration type. " + ex.GetBaseException().ToString());
            }
            return _code;
        }


        /// <summary>
        /// Get Integration type title for sammerarbeid portlen
        /// cap_integration_type2
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        static string getIntegrationTypeForSammerArbeidPorten(string type)
        {
            string _code = "809020000";//DEFAULT
            try
            {
                switch (type.ToUpper())
                {
                    case "IDPORTEN_SAML2":
                        _code = "809020000";
                        break;
                    case "IDPORTEN_SAML2_ONBEHALF":
                        _code = "809020000";
                        break;
                    case "IDPORTEN_OIDC":
                        _code = "809020000";
                        break;
                    case "IDPORTEN_OIDC_ONBEHALFOF":
                        _code = "809020000";
                        break;
                    case "KRR_SOAP":
                        _code = "809020001";
                        break;
                    case "KRR_SOAP_ONBEHALFOF":
                        _code = "809020001";
                        break;
                    case "KRR_REST":
                        _code = "809020001";
                        break;
                    case "KRR_REST_ONBEHALFOF":
                        _code = "809020001";
                        break;


                    case "Digital postkasse":
                        _code = "809020002";
                        break;

                    case "eInnsyn":
                        _code = "809020004";
                        break;

                    case "Signeringstjenesten":
                        _code = "809020005";
                        break;

                    case "EFORMIDLING":
                        _code = "809020003";
                        break;

                    case "EFORMIDLING_DPI":
                        _code = "809020003";
                        break;
                    case "EFORMIDLING_DPO":
                        _code = "809020003";
                        break;
                    case "EFORMIDLING_DPV":
                        _code = "809020003";
                        break;
                    case "EFORMIDLING_DPF":
                        _code = "809020003";
                        break;
                    case "EFORMIDLING_DPE":
                        _code = "809020003";
                        break;

                    default:
                        _code = "809020000";//nothing
                        break;

                }
            }
            catch (Exception ex)
            {

                LogError("getIntegrationTypeForSammerArbeidPorten()", "Reason: error converting integration type. " + ex.GetBaseException().ToString());
                Console.WriteLine("getIntegrationTypeNameByCode()", "Reason: error converting integration type. " + ex.GetBaseException().ToString());
            }
            return _code;
        }


        /// <summary>
        /// trucate the string to specfied length
        /// </summary>
        /// <param name="str"></param>
        /// <param name="maxLength"></param>
        /// <returns></returns>
        static string TruncateLongString(string str, int maxLength)
        {
            return str.Substring(0, Math.Min(str.Length, maxLength));
        }


        /// <summary>
        /// helper copy function for same object
        /// (foreach) was making problem for some records)
        /// </summary>
        /// <param name="objectToCopy"></param>
        /// <returns></returns>
        static IdPortenModel copyObject(IdPortenModel objectToCopy)
        {
            IdPortenModel _finalObject = new IdPortenModel();

            _finalObject.Created = objectToCopy.Created;
            _finalObject.Customer_Organization_Number = objectToCopy.Customer_Organization_Number;
            _finalObject.Description = objectToCopy.Description;
            _finalObject.ID = objectToCopy.ID;
            _finalObject.Integration_Type = objectToCopy.Integration_Type;
            _finalObject.Last_Updated = objectToCopy.Last_Updated;
            _finalObject.Supplier_Integration_Id = objectToCopy.Supplier_Integration_Id;
            _finalObject.Supplier_Organization_Number = objectToCopy.Supplier_Organization_Number;




            return _finalObject;


        }

        /// <summary>
        /// Prepares the records to Processs
        /// </summary>
        /// <param name="records"></param>
        /// <returns></returns>
        static async Task<List<string>> GetIntegrationsToProcess(List<IdPortenModel> records)
        {
            string _error = string.Empty; string _Id = string.Empty; DateTime _lastCleanDate = DateTime.MinValue;
            List<string> _integrationIds = new List<string>(); ApplicationUser _appUser = null; string _deleteLog = string.Empty;
            try
            {
                string apiUrl = Convert.ToString(ConfigurationManager.AppSettings["ApiUrl"]);
                _appUser = await GetIDPortenUser(); // get the application user ID
                if (records.Count > 0) // MUST have integrasjon records from Difi WebAPI, THEN ONLY THEN we execute crm operation )
                {
                    if ((_appUser != null && _appUser.value != null) && (_appUser.value.Count > 0)) //proper userID for appplication user (becuase that user is webapi records owner)
                    {
                        Console.WriteLine("===== found the Application user. =====");
                        Console.WriteLine("===== Creating/Updating records =====");
                        SaveIntegrationRecords(records, _appUser.value[0].systemuserid);
                        Console.WriteLine("===== All finished =====");

                    } // app user
                    else
                    {
                        LogError("=== Failed to get  application user (Azure OAuth) kest-idportenuser@difidrift.onmicrosoft.com ===", string.Empty);
                        Console.WriteLine("===== Failed to get application user (Azure OAuth) kest-idportenuser@difidrift.onmicrosoft.com ====");
                    } //app user

                } //count
                else
                {
                    LogError("=== Difi WebAPI returns NO records ===", string.Empty);
                    Console.WriteLine("=== Difi WebAPI returns NO records ===");
                }
            }
            catch (Exception ex)
            {
                _error = ex.GetBaseException().ToString();
                LogError("Main.Exception.GetIntegrationsToProcess()", _error);
                Console.WriteLine("Main.Exception.GetIntegrationsToProcess()", _error);
            }
            return _integrationIds;
        }

        /// <summary>
        /// get the ID of Application USER (kest-idportenuser@difidrift.onmicrosoft.com)
        /// dedicated to Id-Porten Application
        /// </summary>
        /// <returns></returns>
        static async Task<ApplicationUser> GetIDPortenUser()
        {
            string _error = string.Empty;
            Guid _userId = Guid.Empty; ApplicationUser retrievedUser = null;
            try
            {
                TokenModel _newtoken = getNewToken();
                if (_newtoken != null)
                {
                    using (HttpClient client = new HttpClient())
                    {
                        client.Timeout = new TimeSpan(0, 2, 0);
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _newtoken.Token);
                        client.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
                        client.DefaultRequestHeaders.Add("OData-Version", "4.0");
                        client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                        HttpResponseMessage response = await client.GetAsync(_newtoken.APIUrl + "systemusers?$select=systemuserid&$filter=domainname eq '" + _newtoken.OAuthUserName + "'&$top=1");
                        if (response.Content != null)
                        {
                            if (response.StatusCode == HttpStatusCode.OK)
                            {
                                retrievedUser = JsonConvert.DeserializeObject<ApplicationUser>(
                                   await response.Content.ReadAsStringAsync());

                                if ((retrievedUser != null && retrievedUser.value != null) && (retrievedUser.value.Count > 0)) // send token
                                {
                                    retrievedUser.value[0].token = _newtoken.Token;
                                }
                            }
                            else
                            {
                                Exception ex = new CrmHttpResponseException(response.Content);
                                LogError("GetIDPortenUser(). unable to get application user (Azure) (kest-idportenuser@difidrift.onmicrosoft.com).", ex.GetBaseException().ToString()); //}
                                Console.WriteLine("GetIDPortenUser().unable to get application user (Azure) (kest-idportenuser@difidrift.onmicrosoft.com).", ex.GetBaseException().ToString());
                            }
                        }

                    }//client

                }// token

            }// try
            catch (Exception ex)
            {
                _error = "unable to get application user (Azure) (kest-idportenuser@difidrift.onmicrosoft.com). " + ex.GetBaseException().ToString();
                LogError("Main.Exception.GetIDPortenUser()", _error);
                Console.WriteLine("Main.Exception.GetIDPortenUser()", _error);

            }
            return retrievedUser;
        }

        /// <summary>
        /// read the integration  by Integration name and User
        /// </summary>
        /// <param name="id"></param>
        /// <param name="userId"></param>
        /// <param name="client"></param>
        /// <returns></returns>
        static async Task<Integration_Json_Model> GetIntegrationByIdNameUser(string id, string userId, HttpClient client)
        {
            Integration_Json_Model retrievedintegration = null; string _error = string.Empty;
            try
            {
                string apiUrl = Convert.ToString(ConfigurationManager.AppSettings["ApiUrl"]);

                string url = Uri.EscapeUriString("crayon_tenesteprofil_id eq '" + id + "' and _createdby_value eq " + userId);
                url = url.Replace("'", "%27").Replace("&", "%26");
                HttpResponseMessage response = await client.GetAsync(apiUrl + "crayon_tenesteprofils?$select=crayon_tenesteprofil_id,_cap_leverandrid_value,_crayon_etatid_value,cap_beskrivelse,cap_integration_type,cap_integration_id_title&$filter=" + url + "&$top=1");
                if (response.Content != null)
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        retrievedintegration = JsonConvert.DeserializeObject<Integration_Json_Model>(
                           await response.Content.ReadAsStringAsync(),
                            new JsonSerializerSettings
                            {
                                NullValueHandling = NullValueHandling.Ignore
                            });


                        // set attributes for comapring later
                        if ((retrievedintegration != null && retrievedintegration.value != null) && retrievedintegration.value.Count > 0) // Try to get the relevant organization numbers , if we have Customer and levrøndor
                        {
                            // get teh related custome rand supplier for integration
                            KundeLeveronderModel _result = await getCustomerLevrendor(retrievedintegration.value[0].crayon_tenesteprofilid.ToString(), apiUrl, client);

                            if (_result != null)
                            {
                                retrievedintegration.value[0].Customer_Organization_Number = _result.CustomerOrganizationNumber;
                                retrievedintegration.value[0].accountname = _result.CustomerOrganizationName;
                                retrievedintegration.value[0].CustomerIdGuid = _result.CustomerOrganizationID;
                                //supplier
                                retrievedintegration.value[0].Supplier_Organization_Number = _result.SupplierOrganizationNumber;
                                retrievedintegration.value[0].SupplierIdGuid = _result.SupplierOrganizationID;
                            }

                            if (retrievedintegration.value[0].cap_integration_type > 0)// get integration name
                            {
                                string _integration_type = getIntegrationTypeNameByCode(retrievedintegration.value[0].cap_integration_type);
                                if (!string.IsNullOrWhiteSpace(_integration_type))
                                    retrievedintegration.value[0].Integration_Type = _integration_type;
                            }
                            // integration 
                        }

                    }//ok
                    else
                    {
                        Exception ex = new CrmHttpResponseException(response.Content);
                        _error = string.Format("Id: {0} , Error: {1} ", id, ex.GetBaseException().ToString());
                        LogError("GetIntegrationByIdName(). Failed to read integration record by ID Name.", _error); //}
                        Console.WriteLine("GetIntegrationByIdName()", "Failed to read integration reacord  by ID Name.", _error);
                    }

                }
            }
            catch (Exception ex)
            {
                _error = string.Format("Id: {0} , Error: {1} ", id, ex.GetBaseException().ToString());
                LogError("Main.GetIntegrationByIdName()", "Error in getting  integration record  by ID Name. " + _error);
                Console.WriteLine("Main.GetIntegrationByIdName()", "Error in getting  integration record . ", _error);
            }

            return retrievedintegration;
        }






        /// <summary>
        /// Get all the integrations of user specified
        /// </summary>
        /// <param name="userId">string guid</param>
        /// <param name="client">htpclient</param>
        /// <returns></returns>
        static async Task<Integration_Json_Model> GetAllIntegrationByUser(string userId, HttpClient client)
        {
            Integration_Json_Model retrievedintegration = null; string _error = string.Empty;

            try
            {
                string apiUrl = Convert.ToString(ConfigurationManager.AppSettings["ApiUrl"]);

                string url = Uri.EscapeUriString(" _createdby_value eq " + userId);
                url = url.Replace("'", "%27").Replace("&", "%26");
                HttpResponseMessage response = await client.GetAsync(apiUrl + "crayon_tenesteprofils?$select=crayon_tenesteprofilid,cap_integration_id_title&$filter=" + url);
                if (response.Content != null)
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        retrievedintegration = JsonConvert.DeserializeObject<Integration_Json_Model>(
                           await response.Content.ReadAsStringAsync(),
                            new JsonSerializerSettings
                            {
                                NullValueHandling = NullValueHandling.Ignore
                            });


                    }//ok
                    else
                    {
                        Exception ex = new CrmHttpResponseException(response.Content);
                        _error = string.Format("Error getting alll user integrasjons : {0} ", ex.GetBaseException().ToString());
                        LogError("CrmHttpResponseException(). HttpResponseException.", _error); //}
                        Console.WriteLine("CrmHttpResponseException(). Error getting alll user integrasjons", "HttpResponseException.", _error);
                    }

                }
            }
            catch (Exception ex)
            {
                _error = string.Format("Error: {0} ", ex.GetBaseException().ToString());
                LogError("Main.GetIntegrationByUser()", "Error  getting  integration record  by user. " + _error);
                Console.WriteLine("Main.GetIntegrationByUser()", "Error in getting  integration record by user . ", _error);
            }

            return retrievedintegration;
        }


        /// <summary>
        ///  Get Integration TypeCode by Code
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        static string getIntegrationTypeNameByCode(int code)
        {
            string type = "";//DEFAULT
            try
            {

                switch (code)
                {
                    case 809020000:
                        type = "IDPORTEN SAML2";
                        break;
                    case 809020001:
                        type = "IDPORTEN SAML2 ONBEHALF";
                        break;
                    case 809020002:
                        type = "IDPORTEN OIDC";
                        break;
                    case 809020003:
                        type = "IDPORTEN OIDC ONBEHALFOF";
                        break;
                    case 809020004:
                        type = "KRR SOAP";
                        break;
                    case 809020005:
                        type = "KRR SOAP ONBEHALFOF";
                        break;
                    case 809020006:
                        type = "KRR REST";
                        break;
                    case 809020007:
                        type = "KRR REST ONBEHALFOF";
                        break;

                    case 809020008:
                        type = "Digital postkasse";
                        break;

                    case 809020009:
                        type = "eFormidling";
                        break;
                    case 809020010:
                        type = "eInnsyn";
                        break;

                    case 809020011:
                        type = "Signeringstjenesten";
                        break;
                    case 809020012:
                        type = "eFormidling DPI";
                        break;
                    case 809020013:
                        type = "eformidling DPO";
                        break;
                    case 809020014:
                        type = "eFormidling DPV";
                        break;
                    case 809020015:
                        type = "eFormidling DPF";
                        break;


                }

            }
            catch (Exception ex)
            {

                LogError("getIntegrationTypeNameByCode()", "Reason: error converting integration type. " + ex.GetBaseException().ToString());
                Console.WriteLine("getIntegrationTypeNameByCode()", "Reason: error converting integration type. " + ex.GetBaseException().ToString());
            }
            return type;
        }





        static async Task<RootObject> getOrganizationById(string Id, string apiUrl, HttpClient client)
        {
            RootObject retrievedAccount = null; string _error = string.Empty;
            try
            {

                HttpResponseMessage response = await client.GetAsync(apiUrl + "accounts?$select=accountid,name,crayon_organisasjonsnr,customertypecode&$filter=accountid eq " + Id + "");
                // crayon_tenesteprofils?$top=3                
                if (response.Content != null)
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        retrievedAccount = JsonConvert.DeserializeObject<RootObject>(
                           await response.Content.ReadAsStringAsync(),
                           new JsonSerializerSettings
                           {
                               NullValueHandling = NullValueHandling.Ignore
                           });

                    }
                    else
                    {
                        Exception ex = new CrmHttpResponseException(response.Content);
                        _error = string.Format("Id: {0} , Error: {1} ", Id, ex.GetBaseException().ToString());
                        LogError("getOrganizationById(). Failed to read account by accountID. " + _error, ex.GetBaseException().ToString()); //}
                        Console.WriteLine("getOrganizationById()", "Failed to read account by accountID. " + _error, ex.GetBaseException().ToString());
                    }
                }

            }
            catch (Exception ex)
            {
                _error = string.Format("Id: {0} , Error: {1} ", Id, ex.GetBaseException().ToString());
                LogError("Main.getOrganizationById()", "Error in getting organization number By Id. " + ex.GetBaseException().ToString());
                Console.WriteLine("Main.getOrganizationById()", "Error in getting organization by Id. ", ex.GetBaseException().ToString());
            }

            return retrievedAccount;
        }


        /// <summary>
        /// Deletes the integration record if it exisits
        /// </summary>
        /// <param name="recordid"></param>
        /// <param name="userId"></param>
        /// <param name="client"></param>
        /// <returns></returns>
        static void DeleteIntegration(string recordid, string crayon_tenesteprofilid, HttpClient client)
        {

            string _error = string.Empty; HttpRequestMessage request = null; HttpResponseMessage response = null;
            try
            {
                string apiUrl = Convert.ToString(ConfigurationManager.AppSettings["ApiUrl"]);
                request = new HttpRequestMessage(HttpMethod.Delete, apiUrl + "crayon_tenesteprofils(" + crayon_tenesteprofilid + ")");
                response = client.SendAsync(request).Result;
                if (response.Content != null)
                {
                    if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.OK)
                    {
                        /* retrievedintegration = JsonConvert.DeserializeObject<Integration_Json_Model>(
                            await response.Content.ReadAsStringAsync(),
                             new JsonSerializerSettings
                             {
                                 NullValueHandling = NullValueHandling.Ignore
                             });*/
                    }//ok
                    else
                    {
                        //Exception ex = new CrmHttpResponseException(response.Content);
                        //_error = string.Format("Navn Id: {0} , Actual Guid: {1} , Error: {2} ", recordid, crayon_tenesteprofilid, ex.GetBaseException().ToString());
                        //LogError("DelteIntegrationIfExsits(). Failed to delete  integration record.", _error); //}
                        //Console.WriteLine("DelteIntegrationIfExsits()", "Failed to delete  integration record.", _error);
                    }

                } //if

            }
            catch (Exception ex)
            {
                _error = string.Format("Navn Id: {0} , Actual Guid: {1} , Error: {2} ", recordid, crayon_tenesteprofilid, ex.GetBaseException().ToString());
                LogError("Main.DeleteIntegration()", "Error in deleting  integration record. " + _error);
                Console.WriteLine("Main.DeleteIntegration()", "Error in deleting  integration record . ", _error);
            }
            //return retrievedintegration;
        }


        /// <summary>
        /// Update the integrasjon entity record
        /// </summary>
        /// <param name="retrievedAccount"></param>
        /// <param name="retrievedAccountsupplier"></param>
        /// <param name="client"></param>
        /// <param name="_int"></param>
        /// <param name="apiUrl"></param>
        /// <param name="crmRecord"></param>
        static void UpdateIntegration(RootObject retrievedAccount, RootObject retrievedAccountsupplier, HttpClient client, IdPortenModel _int,
            string apiUrl, Integration_Json_ModelValue crmRecord, string title)
        {
            string _error = string.Empty;
            try
            {
                int result = -1; string _content = "";
                bool needUpdate = false; string _newAccountName = string.Empty;


                JObject _integrationModel = new JObject();


                if ((retrievedAccount != null && retrievedAccount.value != null) && retrievedAccount.value.Count > 0)
                {
                    string _title_ = string.Format("{0} - {1}", _int.ID, retrievedAccount.value[0].name);
                    result = comapreObject(_title_, crmRecord.cap_integration_id_title);
                    if ((result < 0) || (result > 0))
                    {
                        _integrationModel.Add("cap_integration_id_title", title); needUpdate = true;
                    }

                }


                if ((retrievedAccount != null && retrievedAccount.value != null) && retrievedAccount.value.Count > 0)
                {
                    result = comapreObject(_int.Customer_Organization_Number, crmRecord.Customer_Organization_Number);//Customer_Organization_Number
                    if ((result < 0) || (result > 0))
                    {
                        _integrationModel.Add("crayon_etatid@odata.bind", "/accounts(" + retrievedAccount.value[0].accountid + ")"); needUpdate = true;
                        _newAccountName = crmRecord.accountname;/// new account name as customer is changed
                    }
                }

                if ((retrievedAccountsupplier != null && retrievedAccountsupplier.value != null) && retrievedAccountsupplier.value.Count > 0)
                {
                    result = comapreObject(_int.Supplier_Organization_Number, crmRecord.Supplier_Organization_Number);//Supplier_Organization_Number
                    if ((result < 0) || (result > 0))
                    {
                        _integrationModel.Add("cap_LeverandrId@odata.bind", "/accounts(" + retrievedAccountsupplier.value[0].accountid + ")"); needUpdate = true;
                    }
                }

                result = comapreObject(_int.Integration_Type.Replace("_", " "), crmRecord.Integration_Type);//Supplier_Organization_Number
                if ((result < 0) || (result > 0))
                {
                    _integrationModel.Add("cap_integration_type", Convert.ToInt32((getIntegrationTypeCode(_int.Integration_Type)))); needUpdate = true;
                    _integrationModel.Add("cap_integration_type_name", _int.Integration_Type.Replace("_", " "));
                    _integrationModel.Add("cap_integration_type2", Convert.ToInt32((getIntegrationTypeForSammerArbeidPorten(_int.Integration_Type))));
                    _integrationModel.Add("cap_idintegrasjon", _int.ID);//JB added this for the new field ID Integrasjon for updating 

                }

                result = comapreObject(_int.Description, crmRecord.cap_beskrivelse);//cap_beskrivelse
                if ((result < 0) || (result > 0))
                {
                    _integrationModel.Add("cap_beskrivelse", _int.Description); needUpdate = true;
                }

                if (needUpdate) // send for update
                {
                    string integrationUri = apiUrl + "crayon_tenesteprofils(" + crmRecord.crayon_tenesteprofilid + ")";
                    HttpRequestMessage updateRequest = new HttpRequestMessage(new HttpMethod("PATCH"), integrationUri);
                    _content = JsonConvert.SerializeObject(_integrationModel, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
                    updateRequest.Content = new StringContent(_content, Encoding.UTF8, "application/json");
                    HttpResponseMessage updateResponse = client.SendAsync(updateRequest).Result;
                    if (updateResponse.StatusCode == HttpStatusCode.NoContent || updateResponse.StatusCode == HttpStatusCode.OK)
                    {

                    }
                    else
                    {
                        Exception ex = new CrmHttpResponseException(updateResponse.Content);
                        _error = string.Format("Id: {0} , Error: {1}", _int.ID, ex.GetBaseException().ToString());
                        LogError("CrmHttpResponseException(). Failed to update integration record.", _error); //
                        Console.WriteLine("CrmHttpResponseException(). Failed to update integration record.", _error);
                    }
                }

            }
            catch (Exception ex)
            {
                _error = string.Format("Id: {0} , Error: {1}", _int.ID, ex.GetBaseException().ToString());
                LogError("Main.UpdateIntegration()", _error);
                Console.WriteLine("Main.UpdateIntegration()", _error);
            }
        }



        /// <summary>
        /// Comnpare each object and retutn the result
        /// </summary>
        /// <param name="oldIntegrationRecord">First object to Comapre</param>
        /// <param name="_int">Seconed object to comapre</param>
        /// <returns></returns>
        static int comapreObject(string propertyA, string propertyB)
        {
            int result = 0; // equals
            bool result2 = false;
            try
            {
                //JB commented the below code on comparision and rewritten it with Equals as comparison was incorrect
                //result = string.Compare(propertyA, propertyB, StringComparison.OrdinalIgnoreCase);
                result2 = string.Equals(propertyA, propertyB, StringComparison.OrdinalIgnoreCase);

                if (result2 == true)
                    result = 1;

            }
            catch (Exception ex)
            {
                LogError("comapreObjects", ex.GetBaseException().ToString());
            }
            return result;
        }


        static async Task<KundeLeveronderModel> getCustomerLevrendor(string integrationID, string apiUrl, HttpClient client)
        {
            RootObject retrievedAccountInfo = null; string _error = string.Empty; KundeLeveronderModel _result = null;
            try
            {
                HttpResponseMessage response = await client.GetAsync(apiUrl + "cap_tenesteprofil_account_nnset?$select=accountid,cap_tenesteprofil_account_nnid&$filter=crayon_tenesteprofilid eq " + integrationID);
                // crayon_tenesteprofils?$top=3                
                if (response.Content != null)
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        retrievedAccountInfo = JsonConvert.DeserializeObject<RootObject>(
                           await response.Content.ReadAsStringAsync());

                        if ((retrievedAccountInfo != null && retrievedAccountInfo.value != null) && retrievedAccountInfo.value.Count > 0)
                        {
                            _result = new KundeLeveronderModel();
                            foreach (RootObjectValue acc in retrievedAccountInfo.value)
                            {
                                RootObject _account = await getOrganizationById(acc.accountid, apiUrl, client);
                                if ((_account != null && _account.value != null) && _account.value.Count > 0)
                                {
                                    if (_account.value[0].customertypecode == "200000")//kunde
                                    {
                                        _result.CustomerOrganizationNumber = _account.value[0].crayon_organisasjonsnr;
                                        _result.CustomerOrganizationName = _account.value[0].name;
                                        _result.CustomerOrganizationID = _account.value[0].accountid;
                                    }
                                    if (_account.value[0].customertypecode == "10")//levrendor
                                    {
                                        _result.SupplierOrganizationNumber = _account.value[0].crayon_organisasjonsnr;
                                        _result.SupplierOrganizationName = _account.value[0].name;
                                        _result.SupplierOrganizationID = _account.value[0].accountid;
                                    }
                                }

                            }
                        }
                    }
                    else
                    {
                        Exception ex = new CrmHttpResponseException(response.Content);
                        _error = string.Format("Id: {0} , Error: {1} ", integrationID, ex.GetBaseException().ToString());
                        LogError("getCustomerLevrendor(). Failed to get customer levronder.", ex.GetBaseException().ToString()); //}
                        Console.WriteLine("getCustomerLevrendor(). Failed toget  customer levronder.", ex.GetBaseException().ToString());
                    }
                }

            }
            catch (Exception ex)
            {
                _error = string.Format("Id: {0} , Error: {1} ", integrationID, ex.GetBaseException().ToString());
                LogError("Main.getCustomerLevrendor()", "Failed to get customer levronder. " + ex.GetBaseException().ToString());
                Console.WriteLine("Main.getCustomerLevrendor()", "Failed to get customer levronder. " + ex.GetBaseException().ToString());
            }

            return _result;
        }

        #endregion

    }
}


