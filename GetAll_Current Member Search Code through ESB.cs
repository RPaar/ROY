using HealthPartners.XRM.Actions.MemberSearch.Helpers;
using HealthPartners.XRM.Actions.MemberSearch_GetAll.CachePersonByNameService;
using HealthPartners.XRM.Actions.MemberSearch_GetAll.MemberDemographicsService;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.ServiceModel.Channels;


namespace HealthPartners.XRM.Actions.MemberSearch_GetAll
{
    public class GetAll : IPlugin
    {
        ITracingService _tracingService;
        ResponseObject responseObj;
        private static int QUERY_LIMIT = 20;

        public const string busPassObjectName = "BusPass";
        public const string busPassNamespace = "http://www.healthpartners.com/esb/buspass";
        

        public GetAll(string unsecureConfig, string secureConfig)
        {
            string config = string.IsNullOrWhiteSpace(secureConfig) ? (string.IsNullOrWhiteSpace(unsecureConfig) ? string.Empty : unsecureConfig) : secureConfig;

            if (!string.IsNullOrWhiteSpace(config)) {
                string[] info = config.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
                if (info.Length == 1) {
                    QUERY_LIMIT = Int32.Parse(info[0]);
                }
            }
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            _tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            responseObj = new ResponseObject();
            responseObj.DataRows = new List<DataRow>();
            try
            {
                IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
                IOrganizationServiceFactory factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = factory.CreateOrganizationService(context.UserId);

                if (context.Depth > 1)
                    return;

                CommonMethods.WriteLog(_tracingService, "Plugin Invoked");

                if (!context.InputParameters.Contains("RequestObject") || context.InputParameters["RequestObject"] == null)
                {
                    throw new InvalidPluginExecutionException("Error: RequestObject is null or empty");
                }

                CommonMethods.WriteLog(_tracingService, "Getting RequestObject...");
                var inputString = Convert.ToString(context.InputParameters["RequestObject"]);
                var requestObj = CommonMethods.JsonDeserialize<RequestObject>(inputString);
                CommonMethods.WriteLog(_tracingService
                    , string.Format("MemberID: {1}{0} First Name: {2}{0} Middle Initial: {3}{0} Last Name: {4}{0} BirthDate: {5}{0}"
                    , Environment.NewLine, requestObj.MemberID, requestObj.FirstName, requestObj.MiddleInitial, requestObj.LastName, requestObj.BirthDate));
                //refactored code below
                
                //get user name
                string userName = string.Empty;
                var user = service.Retrieve("systemuser", context.UserId, new ColumnSet("fullname"));
                if (user != null)
                {
                    userName = user.GetAttributeValue<string>("fullname");
                }

                           
                getContacts(requestObj, service);
                getLeads(requestObj, service);
                CacheInfo cacheInfo;
                getCacheInfo(service, out cacheInfo);     
                getCache(requestObj, userName, cacheInfo, service);
                

                sortDataRows();
                //CommonMethods.WriteLog(_tracingService, logDataReturning());

                context.OutputParameters["Success"] = true;
                context.OutputParameters["ResponseObject"] = CommonMethods.JsonSerializer(responseObj);

            }
            catch (Exception ex)
            {
                string errorMessage = string.Format("An Exception occurred in {0} plugin. {1} Error Message: {2}", GetType().Name, Environment.NewLine, ex.Message);
                throw new InvalidPluginExecutionException(errorMessage, ex);
            }
        }        

        #region Private Methods

        private void getCacheInfo(IOrganizationService service, out CacheInfo cacheInfo)
        {
            CommonMethods.WriteLog(_tracingService, "getting Cache configuration info..");
            cacheInfo = new CacheInfo();

            QueryExpression cacheConfigQuery = new QueryExpression("po_config")
            {
                ColumnSet = new ColumnSet("po_value", "po_name"),
                Criteria = new FilterExpression()
                {
                    Conditions = {
                        new ConditionExpression(){
                            AttributeName = "po_name",
                            Operator = ConditionOperator.BeginsWith,
                            Values = { "CACHE" }
                        }
                    }
                }
            };

            var results = service.RetrieveMultiple(cacheConfigQuery);

            if (results != null && results.Entities.Count > 0)
            {
                foreach (Entity e in results.Entities)
                {
                    if (e.Attributes.ContainsKey("po_name") && e["po_name"] != null)
                    {
                        var name = e.GetAttributeValue<string>("po_name");
                        if (e.Attributes.ContainsKey("po_value") && e["po_value"] != null)
                        {
                            if (name == "CACHEURL")
                                cacheInfo.URL = e.GetAttributeValue<string>("po_value");
                            else if (name == "CACHEURLDEMO")
                                cacheInfo.Demographics = e.GetAttributeValue<string>("po_value");
                            else if (name == "CACHEURLPERSON")
                                cacheInfo.Person = e.GetAttributeValue<string>("po_value");
                            else if (name == "CACHEEnvironment")
                                cacheInfo.Environment = e.GetAttributeValue<string>("po_value");
                        }
                    }
                }
            }
            CommonMethods.WriteLog(_tracingService, string.Format("CacheInfo {0}{0} URL: {1}{0} Demographics: {2}{0} Person: {3}{0}"
                , Environment.NewLine, cacheInfo.URL, cacheInfo.Demographics, cacheInfo.Person));
        }

        private void getContacts(RequestObject requestObj, IOrganizationService service)
        {
            CommonMethods.WriteLog(_tracingService, "getting Contacts..");
            QueryExpression contactsQuery = new QueryExpression("contact")
            {
                ColumnSet = new ColumnSet("contactid", "po_memberid", "firstname", "lastname", "birthdate"
                    , "address1_line1", "address1_city", "address1_stateorprovince", "address1_postalcode")
            };

            var filter = new FilterExpression()
            {
                FilterOperator = LogicalOperator.And,
                Conditions = {
                    new ConditionExpression(){
                        AttributeName = "statecode",
                        Operator = ConditionOperator.Equal,
                        Values = {0}
                    }
                }
            };

            if (!string.IsNullOrEmpty(requestObj.MemberID))
            {
                filter.AddCondition(new ConditionExpression()
                {
                    AttributeName = "po_memberid",
                    Operator = ConditionOperator.Equal,
                    Values = { requestObj.MemberID }
                });
            }

            if (!string.IsNullOrEmpty(requestObj.LastName))
            {
                filter.AddCondition(new ConditionExpression()
                {
                    AttributeName = "lastname",
                    Operator = ConditionOperator.BeginsWith,
                    Values = { requestObj.LastName }
                });
            }

            if (!string.IsNullOrEmpty(requestObj.BirthDate))
            {
                var birthday = DateTime.Parse(requestObj.BirthDate);
                filter.AddCondition(new ConditionExpression()
                {
                    AttributeName = "birthdate",
                    Operator = ConditionOperator.Equal,
                    Values = { birthday }
                });
            }

            if (!string.IsNullOrEmpty(requestObj.FirstName))
            {
                filter.AddCondition(new ConditionExpression()
                {
                    AttributeName = "firstname",
                    Operator = ConditionOperator.BeginsWith,
                    Values = { requestObj.FirstName }
                });
            }

            filter.AddCondition(new ConditionExpression()
            {
                AttributeName = "po_leadtype",
                Operator = ConditionOperator.Equal,
                Values = { 2 }
            });
            

            contactsQuery.Criteria.AddFilter(filter);
            contactsQuery.PageInfo = new PagingInfo()
            {
                Count = QUERY_LIMIT,
                PageNumber = 1,
                ReturnTotalRecordCount = true
            };

            var results = service.RetrieveMultiple(contactsQuery);

            if (results != null && results.Entities.Count >= 1)
            {
                responseObj.TotalFound = results.TotalRecordCount;

                foreach (var entity in results.Entities)
                {
                    var row = new DataRow();
                    row.Source = "";
                    row.Source = "CRM";
                    row.ContactLead = "Contact";
                    row.ContactId = entity.GetAttributeValue<Guid>("contactid").ToString();
                    row.MemberId = entity.GetAttributeValue<string>("po_memberid");
                    row.FirstName = entity.GetAttributeValue<string>("firstname");
                    row.LastName = entity.GetAttributeValue<string>("lastname");
                    row.Street1 = entity.GetAttributeValue<string>("address1_line1");
                    var birthdate = entity.GetAttributeValue<DateTime>("birthdate");
                    if (birthdate > new DateTime())
                        row.BirthDate = birthdate.ToString("yyyy-MM-dd");
                    row.City = entity.GetAttributeValue<string>("address1_city");
                    row.State = entity.GetAttributeValue<string>("address1_stateorprovince");
                    row.Zip = entity.GetAttributeValue<string>("address1_postalcode");
                    responseObj.DataRows.Add(row);
                }
            }
        }

        private void getLeads(RequestObject requestObj, IOrganizationService service)
        {

            CommonMethods.WriteLog(_tracingService, "getting Leads..");
            var leadQuery = new QueryExpression("lead")
            {
                ColumnSet = new ColumnSet("leadid", "firstname", "lastname", "address1_line1", "address1_city", "address1_stateorprovince", "address1_postalcode", "po_birthdate", "po_memberid")
            };

            var filter = new FilterExpression()
            {
                FilterOperator = LogicalOperator.And,
                Conditions = {
                    new ConditionExpression(){
                        AttributeName = "statecode",
                        Operator = ConditionOperator.Equal,
                        Values = {0}
                    }
                }
            };

            if (requestObj.MemberID != null && requestObj.MemberID != string.Empty)
            {
                filter.AddCondition(new ConditionExpression()
                {
                    AttributeName = "po_memberid",
                    Operator = ConditionOperator.Equal,
                    Values = { requestObj.MemberID }
                });
            }

            if (!string.IsNullOrEmpty(requestObj.LastName))
            {
                filter.AddCondition(new ConditionExpression()
                {
                    AttributeName = "lastname",
                    Operator = ConditionOperator.BeginsWith,
                    Values = { requestObj.LastName }
                });                
            }

            if (!string.IsNullOrEmpty(requestObj.BirthDate))
            {
                var birthday = DateTime.Parse(requestObj.BirthDate);
                filter.AddCondition(new ConditionExpression()
                {
                    AttributeName = "po_birthdate",
                    Operator = ConditionOperator.Equal,
                    Values = { birthday }
                });
            }

            if (requestObj.FirstName != string.Empty)
            {
                filter.AddCondition(new ConditionExpression()
                {
                    AttributeName = "firstname",
                    Operator = ConditionOperator.BeginsWith,
                    Values = { requestObj.FirstName }
                });
            }

            leadQuery.Criteria.AddFilter(filter);
            leadQuery.PageInfo = new PagingInfo()
            {
                Count = QUERY_LIMIT,
                PageNumber = 1,
                ReturnTotalRecordCount = true
            };

            var results = service.RetrieveMultiple(leadQuery);

            if (results != null && results.Entities.Count >= 1)
            {
                responseObj.TotalFound += results.TotalRecordCount;

                List<DataRow> rows = new List<DataRow>();

                foreach (var entity in results.Entities)
                {
                    var row = new DataRow();
                    row.Source = "CRM";
                    row.ContactLead = "Lead";
                    row.LeadId = entity.GetAttributeValue<Guid>("leadid").ToString();
                    row.MemberId = entity.GetAttributeValue<string>("po_memberid");
                    row.FirstName = entity.GetAttributeValue<string>("firstname");
                    row.LastName = entity.GetAttributeValue<string>("lastname");
                    row.Street1 = entity.GetAttributeValue<string>("address1_line1");
                    var birthdate = entity.GetAttributeValue<DateTime>("po_birthdate");
                    if (birthdate > new DateTime())
                        row.BirthDate = birthdate.ToString("yyyy-MM-dd");
                    row.City = entity.GetAttributeValue<string>("address1_city");
                    row.State = entity.GetAttributeValue<string>("address1_stateorprovince");
                    row.Zip = entity.GetAttributeValue<string>("address1_postalcode");
                    responseObj.DataRows.Add(row);
                }
            }
        }

        private void getCache(RequestObject requestObj, string userName, CacheInfo cacheInfo, IOrganizationService service)
        {
            CommonMethods.WriteLog(_tracingService, "getting Cache..");
            var myBinding = new BasicHttpBinding();
            myBinding.MaxReceivedMessageSize = 100000;
            // var cachePersonEndpoint = new EndpointAddress(cacheInfo.URL + cacheInfo.Person);
            // var cacheDemoEndpoint = new EndpointAddress(cacheInfo.URL + cacheInfo.Demographics);
            // var cachePersonService = new CachePersonByNameService.CachePersonByNameServiceSoapClient(myBinding, cachePersonEndpoint);
            // var demographicService = new MemberDemographicsService.MemberDemographicsServiceSoapClient(myBinding, cacheDemoEndpoint);


            //Begin new ESB variables
            HttpBindingBase binding;
            EndpointAddress endpoint;
            string BusPassEnvironment = cacheInfo.Environment;
            string EndPointMemberDemo = cacheInfo.Demographics;// "https://devbus.healthpartners.com/Gateway/Membership/MemberDemographicsService";
            string PortTypeMemberDemo = "MemberDemographicsServiceSoap";
            string BusPassNameSpaceMemberDemo = "MemberDemographicsService";
            string EndPointPersonByName = cacheInfo.Person; // "https://devbus.healthpartners.com/Gateway/Membership/PersonByNameService";
            string PortTypePersonByName = "CachePersonByNameServiceSoap";
            string BusPassNameSpacePersonByName = "PersonByNameService";
            //End new ESB variables

            // Create CRM log entity to capture that the Cache Web Service was queried against.
            var entity = new Entity();
            entity.LogicalName = "po_cacheauditlog";
            entity["po_name"] = userName;
            entity["po_action"] = new OptionSetValue() { Value = 100000000 };
            entity["po_description"] = string.Format("Member ID: {0}, First Name: {1}, Last Name: {2}, Birth Date {3}", requestObj.MemberID, requestObj.FirstName, requestObj.LastName, requestObj.BirthDate );

            if (service.Create(entity) == Guid.Empty)
                throw new InvalidOperationException("Unable to log cache web service call");
            
            var matchRequest = new PersonMatchRequest();

            if (!string.IsNullOrEmpty(requestObj.FirstName))
            {
                matchRequest.firstName = requestObj.FirstName;
            }
            if (!string.IsNullOrEmpty(requestObj.LastName))
            {
                matchRequest.lastName = requestObj.LastName;
            }
            if (!string.IsNullOrEmpty(requestObj.MiddleInitial))
            {
                matchRequest.midInitial = requestObj.MiddleInitial;
            }

            var parsedBirthdate = new DateTime();
            if (requestObj.BirthDate != null && DateTime.TryParse(requestObj.BirthDate, out parsedBirthdate))
            {
                matchRequest.birthDate = parsedBirthdate;
                matchRequest.birthDateSpecified = true;
            }
            if (!string.IsNullOrEmpty(requestObj.MemberID))
            {
                createBusPass(EndPointMemberDemo, PortTypeMemberDemo, BusPassNameSpaceMemberDemo, BusPassEnvironment, out binding, out endpoint);
                var serviceClientDemographic = new ChannelFactory<MemberDemographicsServiceSoapChannel>(binding, endpoint).CreateChannel();
                var memberIDmatchRequest = new getMemberDemographicsRequest();
                memberIDmatchRequest.Body = new getMemberDemographicsRequestBody();
                memberIDmatchRequest.Body.personId = requestObj.MemberID;
                getMemberDemographics(memberIDmatchRequest, serviceClientDemographic);
            }

            matchRequest.maxReturnCount = QUERY_LIMIT;
            matchRequest.maxReturnCountSpecified = true;

            CommonMethods.WriteLog(_tracingService, string.Format("matchRequest {0}{0} first name: {1}{0} middle initial: {2}{0} last name: {3}{0} birthdate: {4}{0} return count: {5}{0}"
                , Environment.NewLine, matchRequest.firstName, matchRequest.midInitial, matchRequest.lastName, matchRequest.birthDate, matchRequest.maxReturnCount));
            createBusPass(EndPointPersonByName, PortTypePersonByName, BusPassNameSpacePersonByName, BusPassEnvironment, out binding, out endpoint);
            var serviceClient = new ChannelFactory<CachePersonByNameServiceSoapChannel>(binding, endpoint).CreateChannel();
            getPersonMatches(serviceClient, matchRequest);
        }

        private void getMemberDemographics(getMemberDemographicsRequest requestObj, MemberDemographicsServiceSoapChannel demographicService)
        {
            CommonMethods.WriteLog(_tracingService, "getting member demographics..");
            getMemberDemographicsResponse response = demographicService.getMemberDemographics(requestObj);

            if (!string.IsNullOrEmpty(response.Body.getMemberDemographicsResult.errorDescription))
                throw new InvalidOperationException("Cache Demographic Service Error: " + response.Body.getMemberDemographicsResult.errorDescription);

            responseObj.TotalFound += 1;
            var row = new DataRow();

            row.Source = "Cache";
            row.BirthDate = response.Body.getMemberDemographicsResult.personDateOfBirth;
            row.City = ProperCase(response.Body.getMemberDemographicsResult.personCity);
            row.FirstName = ProperCase(response.Body.getMemberDemographicsResult.personFirstName);
            row.LastName = ProperCase(response.Body.getMemberDemographicsResult.personLastName);
            row.MemberId = response.Body.getMemberDemographicsResult.personExternalId;
            row.State = response.Body.getMemberDemographicsResult.personState;
            row.Street1 = ProperCase(response.Body.getMemberDemographicsResult.personStreetAddressLine1);
            row.Zip = response.Body.getMemberDemographicsResult.personZipCode;
            responseObj.DataRows.Add(row);
        }

        private void getPersonMatches(CachePersonByNameServiceSoapChannel cachePersonService, PersonMatchRequest matchRequest)
        {
            CommonMethods.WriteLog(_tracingService, "getting person matches..");

            PersonMatches personMatches = cachePersonService.getPersonMatchesLimitedList( matchRequest );

            if (personMatches.totalMatchesFound > 0)
            {
                CommonMethods.WriteLog(_tracingService, string.Format("total person matches found: {1}{0} total returned: {2}{0}", Environment.NewLine, personMatches.totalMatchesFound, personMatches.match.Count()));
                responseObj.TotalFound += (int)personMatches.totalMatchesFound;
                foreach (var cachePerson in personMatches.match)
                {
                    var row = new DataRow();
                    row.Source = "Cache";
                    row.BirthDate = Convert.ToString(cachePerson.demographics.birthDate);
                    row.City = ProperCase(cachePerson.home.city);
                    row.FirstName = ProperCase(cachePerson.demographics.firstName);
                    row.LastName = ProperCase(cachePerson.demographics.lastName);
                    row.MemberId = cachePerson.demographics.memberNumber;
                    row.State = cachePerson.home.state;
                    row.Street1 = ProperCase(cachePerson.home.streetLine1);
                    row.Zip = cachePerson.home.zip;
                    responseObj.DataRows.Add(row);
                }
            }
        }

        public void createBusPass(string inEndPoint, string inPortType, string inBusinessNameSpace, string inBusPassEnvironment, out HttpBindingBase binding, out EndpointAddress endpoint)
        {
            string endPoint = inEndPoint;

            HPBusPass busPass = new HPBusPass()
            {
                environment = inBusPassEnvironment,
                version = "1.0",
                group = "SalesCRM",
                application = "CRM-2011",
                uniqueToken = "Jj45HFavhQcSfvaj",
                portType = inPortType,
                buspassNamespace = inBusinessNameSpace,
                mockdata = "false"
            };
            //Get a URI for the endpoint.
            Uri serviceUri = new Uri(endPoint);

       //     HttpBindingBase binding;

            if (!String.IsNullOrEmpty(Uri.UriSchemeHttps))
            {
                binding = new BasicHttpsBinding();
            }
            else
            {
                binding = new BasicHttpBinding();
            }

            long? maxReceivedMessageSize = 2147483647;
            if (maxReceivedMessageSize != null)
            {
                binding.MaxReceivedMessageSize = (long)maxReceivedMessageSize;
            }

            int? maxBufferSize = 2147483647;
            if (maxBufferSize != null)
            {
                binding.MaxBufferSize = (int)maxBufferSize;
            }

            long? maxBufferPoolSize = 2147483647;
            if (maxBufferPoolSize != null)
            {
                binding.MaxBufferPoolSize = (long)maxBufferPoolSize;
            }

            AddressHeader header = AddressHeader.CreateAddressHeader(busPassObjectName, busPassNamespace, busPass);

            endpoint = new EndpointAddress(serviceUri, new[] { header });
            return;

        }
        private string ProperCase(string str)
        {
            if (string.IsNullOrEmpty(str))
                return string.Empty;

            return Regex.Replace(str, @"\w+", (m) =>
            {
                string tmp = m.Value;
                return char.ToUpper(tmp[0]) + tmp.Substring(1, tmp.Length - 1).ToLower();
            });
        }
        
        private void sortDataRows()
        {
            //sort missing lastname rows
            var missingLastNameRows = responseObj.DataRows.Where(r => string.IsNullOrEmpty(r.LastName)).ToList<DataRow>();
            missingLastNameRows.Sort((x, y) =>
            {
                if(!string.IsNullOrEmpty(x.FirstName) && !string.IsNullOrEmpty(y.FirstName))
                    return x.FirstName.ToLower().CompareTo(y.FirstName.ToLower());
                else                
                    return 0;               
            });

            var withLastNameRows = responseObj.DataRows.Where(r => !string.IsNullOrEmpty(r.LastName)).ToList<DataRow>();

            //Sort by first name then by last name 
            withLastNameRows.Sort((x, y) =>
            {
                //able to use toLower if both have a first name. Last name guaranteed in this collection
                if (!string.IsNullOrEmpty(x.FirstName) && !string.IsNullOrEmpty(y.FirstName))
                {
                    if (!x.FirstName.ToLower().Equals(y.FirstName.ToLower()))
                        return x.FirstName.ToLower().CompareTo(y.FirstName.ToLower());
                    else                    
                        return x.LastName.ToLower().CompareTo(y.LastName.ToLower());                    
                }
                else if (!string.IsNullOrEmpty(x.FirstName))
                { 
                    return -1;                                                
                }
                else if (!string.IsNullOrEmpty(y.FirstName))
                {                    
                    return 1;                    
                }
                else
                    return x.LastName.ToLower().CompareTo(y.LastName.ToLower());
            });
  
            responseObj.DataRows.Clear();

            responseObj.DataRows.AddRange(missingLastNameRows);
            responseObj.DataRows.AddRange(withLastNameRows);
        }

        private string logDataReturning()
        {
            var sb = new StringBuilder();
            foreach (var row in responseObj.DataRows)
            {
                sb.Append(string.Format("Source: {1}{0} ContactLead: {2}{0} MemberId: {3}{0} FirstName: {4}{0} LastName: {5}{0} BirthDate: {6}{0} Street1: {7}{0} City: {8}{0} State: {9}{0} Zip: {10}{0} LeadId: {11}{0} MemberId: {12}{0} {0}"
                    , Environment.NewLine, row.Source, row.ContactLead, row.MemberId, row.FirstName, row.LastName, row.BirthDate
                    , row.Street1, row.City, row.State, row.Zip, row.LeadId, row.MemberId));
            }
            return sb.ToString();
        }


        #endregion

        #region Models
        public class CacheInfo {
            public string Name { get; set; }
            public string URL { get; set; }
            public string Demographics { get; set; }
            public string Person { get; set; }
            public string Environment { get; set; }
        }

        public class DataRow
        {
            public string Source { get; set; }
            public string ContactLead { get; set; }
            public string MemberId { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string BirthDate { get; set; }
            public string Street1 { get; set; }
            public string City { get; set; }
            public string State { get; set; }
            public string Zip { get; set; }
            public string ContactId { get; set; }
            public string LeadId { get; set; }
        }
        #endregion

        #region Request/Response classes
        [DataContract]
        public class RequestObject
        {
            [DataMember]
            public string MemberID { get; set; }
            [DataMember]
            public string FirstName { get; set; }
            [DataMember]
            public string MiddleInitial { get; set; }
            [DataMember]
            public string LastName { get; set; }
            [DataMember]
            public string BirthDate { get; set; }           
        }
        
        [DataContract]
        public class ResponseObject
        {
            [DataMember]
            public int TotalFound { get; set; }
            [DataMember]
            public List<DataRow> DataRows { get; set; }
        }

        [DataContract(Namespace = "http://www.healthpartners.com/esb/buspass")]
        public class HPBusPass
        {
            [DataMember(Name = "environment")]
            public string environment { get; set; }
            [DataMember(Name = "version")]
            public string version { get; set; }
            [DataMember(Name = "group")]
            public string group { get; set; }
            [DataMember(Name = "application")]
            public string application { get; set; }
            [DataMember(Name = "uniqueToken")]
            public string uniqueToken { get; set; }
            [DataMember(Name = "portType")]
            public string portType { get; set; }
            [DataMember(Name = "buspassNamespace")]
            public string buspassNamespace { get; set; }
            [DataMember(Name = "mockdata")]
            public string mockdata { get; set; }
        }
        #endregion
    }
}
