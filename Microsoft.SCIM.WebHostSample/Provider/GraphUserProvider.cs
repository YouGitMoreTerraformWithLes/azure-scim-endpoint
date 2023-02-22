using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using Microsoft.Graph;
using Microsoft.SCIM;
using Newtonsoft.Json;

namespace Microsoft.SCIM.WebHostSample.Provider
{
    public class GraphUserProvider : ProviderBase
    {
        private readonly GraphServiceClient _graphServiceClient;

        public GraphUserProvider(GraphServiceClient graphServiceClient)
        {
            _graphServiceClient = graphServiceClient;
        }

        public override async Task<Resource> CreateAsync(Resource resource, string correlationIdentifier)
        {
            if (resource.Identifier != null)
            {
                throw new HttpResponseException(HttpStatusCode.BadRequest);
            }

            Core2EnterpriseUser user = resource as Core2EnterpriseUser;
            if (string.IsNullOrWhiteSpace(user.UserName))
            {
                throw new HttpResponseException(HttpStatusCode.BadRequest);
            }

            var queryOptions = new List<QueryOption>()
                    {
                        new QueryOption("$count", "true")
                    };
            var users = await _graphServiceClient.Users
                .Request(queryOptions)
                .Header("ConsistencyLevel", "eventual")
                .GetAsync();

            if (users.Any(a => string.Equals(a.UserPrincipalName, user.UserName, StringComparison.Ordinal)))
            {
                throw new HttpResponseException(HttpStatusCode.Conflict);
            }
            var mailNickname = user.UserName.Split('@').First();
            var graphUser = new Graph.User
            {
                AccountEnabled = true,
                DisplayName = user.DisplayName,
                MailNickname = mailNickname,
                UserPrincipalName = user.UserName,
                JobTitle = user.Title,
                PasswordProfile = new PasswordProfile
                {
                    ForceChangePasswordNextSignIn = true,
                    Password = "pass123@@@"
                }
            };
            var names = user.DisplayName.Split(' ');
            if (names.Any() && names.Length > 0)
            {
                graphUser.GivenName = names.FirstOrDefault();
                graphUser.Surname = names.LastOrDefault();
            }

            var otherMails = new List<string>();
            if (user.ElectronicMailAddresses != null)
            {
                foreach (var mail in user.ElectronicMailAddresses)
                {
                    if (mail.Primary)
                    {
                        graphUser.Mail = mail.Value;
                    }
                    else
                    {
                        otherMails.Add(mail.Value);
                    }
                }
            }
            if (otherMails.Any())
            {
                graphUser.OtherMails = otherMails;
            }

            if (user.PhoneNumbers != null)
            {
                foreach (var phone in user.PhoneNumbers)
                {
                    if (phone.ItemType == PhoneNumber.Mobile)
                    {
                        graphUser.MobilePhone = phone.Value;
                    }
                    else
                    {
                        graphUser.BusinessPhones = new List<string> { phone.Value };
                    }
                }
            }

            var result = await _graphServiceClient.Users
               .Request()
               .AddAsync(graphUser);

            var userId = result.Id;

            resource.Identifier = result.Id;

            return resource;

        }


        public override async Task DeleteAsync(IResourceIdentifier resourceIdentifier, string correlationIdentifier)
        {
            if (string.IsNullOrWhiteSpace(resourceIdentifier?.Identifier))
            {
                throw new HttpResponseException(HttpStatusCode.BadRequest);
            }

            string identifier = resourceIdentifier.Identifier;

            await _graphServiceClient.Users[identifier]
            .Request()
            .DeleteAsync();


        }

        public override async Task<Resource> RetrieveAsync(IResourceRetrievalParameters parameters, string correlationIdentifier)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (string.IsNullOrWhiteSpace(correlationIdentifier))
            {
                throw new ArgumentNullException(nameof(correlationIdentifier));
            }

            if (string.IsNullOrEmpty(parameters?.ResourceIdentifier?.Identifier))
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            string identifier = parameters.ResourceIdentifier.Identifier;

            var user = await _graphServiceClient.Users[identifier]
            .Request()
            .Select("id,displayName,userPrincipalName,givenName,surName,mail,otherMails,identities,accountEnabled,jobTitle,department,businessPhones,mobilePhone,streetAddress,createdDateTime")
            .GetAsync();

            if (user != null)
            {
                var c2User = new Core2EnterpriseUser
                {
                    Active = user.AccountEnabled.HasValue ? user.AccountEnabled.Value : false,
                    Identifier = user.Id,
                    DisplayName = user.DisplayName,
                    UserName = user.UserPrincipalName,
                    UserType = user.ODataType
                };
                var name = new Name
                {
                    GivenName = user.GivenName,
                    FamilyName = user.Surname
                };
                var phones = new List<PhoneNumber>();
                if (user.BusinessPhones.Any())
                {
                    foreach (var phone in user.BusinessPhones)
                    {
                        phones.Add(new PhoneNumber
                        {
                            ItemType = PhoneNumber.Work,
                            Primary = string.IsNullOrEmpty(user.MobilePhone) ? true : false,
                            Value = phone
                        });
                    }
                }
                if (!string.IsNullOrEmpty(user.MobilePhone))
                {
                    phones.Add(new PhoneNumber
                    {
                        ItemType = PhoneNumber.Mobile,
                        Primary = true,
                        Value = user.MobilePhone
                    });
                }
                c2User.PhoneNumbers = phones.Any() ? phones : null;
                c2User.Name = name;
                var mails = new List<ElectronicMailAddress>();
                if (user.OtherMails.Any())
                {
                    foreach (var mail in user.OtherMails)
                    {
                        mails.Add(new ElectronicMailAddress
                        {
                            ItemType = ElectronicMailAddress.Other,
                            Primary = false,
                            Value = mail
                        });
                    }
                }
                mails.Add(new ElectronicMailAddress
                {
                    ItemType = ElectronicMailAddress.Work,
                    Primary = true,
                    Value = user.Mail
                });
                c2User.ElectronicMailAddresses = mails;
                if (user.StreetAddress != null)
                {
                    var streetAddress = new List<Address>();
                    streetAddress.Add(new Address { StreetAddress = user.StreetAddress, ItemType = Address.Home });
                    c2User.Addresses = streetAddress;
                }
                c2User.Title = user.JobTitle;
                c2User.EnterpriseExtension.Department = user.Department;

                if (user.CreatedDateTime.HasValue)
                {
                    c2User.Metadata.Created = user.CreatedDateTime.Value.DateTime;
                }

                var roles = await _graphServiceClient
                 .Users[identifier].AppRoleAssignments
                 .Request()
                 .GetAsync();

                var userRoles = roles.ToList();
                if (userRoles.Any())
                {
                    var c2Roles = new List<Role>();
                    foreach (var role in userRoles)
                    {
                        c2Roles.Add(new Role { Display = role.PrincipalDisplayName, ItemType = role.PrincipalType, Value = role.ResourceDisplayName });
                    }
                    c2User.Roles = c2Roles;
                }

                return c2User;
            }

            throw new HttpResponseException(HttpStatusCode.NotFound);
        }

        public override async Task UpdateAsync(IPatch patch, string correlationIdentifier)
        {
            if (null == patch)
            {
                throw new ArgumentNullException(nameof(patch));
            }

            if (null == patch.ResourceIdentifier)
            {
                throw new ArgumentException(string.Format(SystemForCrossDomainIdentityManagementServiceResources.ExceptionInvalidOperation));
            }

            if (string.IsNullOrWhiteSpace(patch.ResourceIdentifier.Identifier))
            {
                throw new ArgumentException(SystemForCrossDomainIdentityManagementServiceResources.ExceptionInvalidOperation);
            }

            if (null == patch.PatchRequest)
            {
                throw new ArgumentException(SystemForCrossDomainIdentityManagementServiceResources.ExceptionInvalidOperation);
            }

            var patchRequest = patch.PatchRequest as PatchRequest2;

            if (null == patchRequest)
            {
                string unsupportedPatchTypeName = patch.GetType().FullName;
                throw new NotSupportedException(unsupportedPatchTypeName);
            }

            if (null == patchRequest.Operations || !patchRequest.Operations.Any())
            {
                throw new NotSupportedException("Patch request contains no opertaions.");
            }

            var user = new Graph.User();
            Apply(ref user, patchRequest);

            try
            {
                await _graphServiceClient.Users[patch.ResourceIdentifier.Identifier]
               .Request()
               .UpdateAsync(user);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

        }

        public static void Apply(ref Graph.User user, PatchRequest2 patch)
        {
            if (null == user)
            {
                throw new ArgumentNullException(nameof(user));
            }

            if (null == patch)
            {
                return;
            }

            if (null == patch.Operations || !patch.Operations.Any())
            {
                return;
            }

            foreach (PatchOperation2Combined operation in patch.Operations)
            {
                PatchOperation2 operationInternal = new PatchOperation2()
                {
                    OperationName = operation.OperationName,
                    Path = operation.Path
                };

                OperationValue[] values =
                    JsonConvert.DeserializeObject<OperationValue[]>(
                        operation.Value,
                        ProtocolConstants.JsonSettings.Value);

                if (values == null)
                {
                    string value =
                        JsonConvert.DeserializeObject<string>(operation.Value, ProtocolConstants.JsonSettings.Value);
                    OperationValue valueSingle = new OperationValue()
                    {
                        Value = value
                    };
                    operationInternal.AddValue(valueSingle);
                }
                else
                {
                    foreach (OperationValue value in values)
                    {
                        operationInternal.AddValue(value);
                    }
                }

                Apply(ref user, operationInternal);
            }
        }

        private static void Apply(ref Graph.User user, PatchOperation2 operation)
        {
            if (null == operation)
            {
                return;
            }

            if (null == operation.Path || string.IsNullOrWhiteSpace(operation.Path.AttributePath))
            {
                return;
            }

            OperationValue value;
            switch (operation.Path.AttributePath)
            {
                case AttributeNames.Active:
                    if (operation.Name != OperationName.Remove)
                    {
                        value = operation.Value.SingleOrDefault();
                        if (value != null && !string.IsNullOrWhiteSpace(value.Value) && bool.TryParse(value.Value, out bool active))
                        {
                            user.AccountEnabled = active;
                        }
                    }
                    break;

                case AttributeNames.Addresses:
                    if (OperationName.Remove == operation.Name)
                    {
                        //can't remove it apparently
                    }
                    else
                    {
                        value = operation.Value.SingleOrDefault();
                        user.StreetAddress = value.Value;
                    }

                    break;

                case AttributeNames.DisplayName:

                    if (OperationName.Remove != operation.Name)
                    {
                        value = operation.Value.SingleOrDefault();
                        user.DisplayName = value.Value;
                    }

                    break;

                case AttributeNames.ElectronicMailAddresses:
                    value = operation.Value.FirstOrDefault();
                    user.Mail = value.Value;
                    break;


                case AttributeNames.Name:
                    if (OperationName.Replace == operation.Name)
                    {
                        string givenName;
                        if
                        (
                            string.Equals(
                                AttributeNames.GivenName,
                                operation.Path.ValuePath.AttributePath,
                                StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            givenName = operation.Value?.Single().Value;
                            user.GivenName = givenName;
                        }
                        string familyName;
                        if
                        (
                            string.Equals(
                                AttributeNames.FamilyName,
                                operation.Path.ValuePath.AttributePath,
                                StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            familyName = operation.Value?.Single().Value;
                            user.Surname = familyName;
                        }
                    }

                    break;

                case AttributeNames.PhoneNumbers:
                    IFilter subAttribute = operation.Path.SubAttributes.SingleOrDefault();
                    if (null == subAttribute)
                    {
                        break;
                    }
                    string phoneNumberType = subAttribute.ComparisonValue;

                    if (OperationName.Remove == operation.Name)
                    {
                        //remove
                        if (!string.Equals(phoneNumberType, PhoneNumber.Mobile, StringComparison.Ordinal))
                        {
                            //don't allow mobile to be removed
                            user.BusinessPhones = new List<string>(); //clear biz phones
                            break;
                        }
                    }
                    else
                    {
                        //update
                        if (string.Equals(phoneNumberType, PhoneNumber.Mobile, StringComparison.Ordinal))
                        {
                            string mobile = operation.Value?.Single().Value;
                            user.MobilePhone = mobile;
                            break;
                        }
                        if (string.Equals(phoneNumberType, PhoneNumber.Work, StringComparison.Ordinal))
                        {
                            string work = operation.Value?.Single().Value;
                            user.BusinessPhones = new List<string> { work };
                            break;
                        }
                    }

                    break;

                case AttributeNames.PreferredLanguage:
                    value = operation.Value.SingleOrDefault();

                    if (OperationName.Remove == operation.Name)
                    {
                        if ((null == value) || string.Equals(user.PreferredLanguage, value.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            value = null;
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (null == value)
                    {
                        user.PreferredLanguage = null;
                    }
                    else
                    {
                        user.PreferredLanguage = value.Value;
                    }
                    break;

                //case AttributeNames.Roles:
                //    //user.PatchRoles(operation);
                //    break;

                case AttributeNames.Title:
                    value = operation.Value.SingleOrDefault();

                    if (OperationName.Remove == operation.Name)
                    {
                        if ((null == value) || string.Equals(user.JobTitle, value.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            value = null;
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (null == value)
                    {
                        user.JobTitle = null;
                    }
                    else
                    {
                        user.JobTitle = value.Value;
                    }
                    break;

                case AttributeNames.Department:
                    value = operation.Value.SingleOrDefault();

                    if (OperationName.Remove == operation.Name)
                    {
                        if ((null == value) || string.Equals(user.Department, value.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            value = null;
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (null == value)
                    {
                        user.Department = null;
                    }
                    else
                    {
                        user.Department = value.Value;
                    }
                    break;

                case AttributeNames.UserName:
                    value = operation.Value.SingleOrDefault();

                    if (OperationName.Remove != operation.Name)
                    {
                        user.UserPrincipalName = value.Value;
                    }


                    break;
            }
        }



    }
}
