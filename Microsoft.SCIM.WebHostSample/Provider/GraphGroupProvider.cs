using Microsoft.Graph;
using System.Collections.Generic;
using System.Net;
using System;
using System.Threading.Tasks;
using System.Web.Http;
using System.Linq;
using Newtonsoft.Json;
using System.Net.Http;

namespace Microsoft.SCIM.WebHostSample.Provider
{
    public class GraphGroupProvider : ProviderBase
    {
        private readonly GraphServiceClient _graphServiceClient;

        public GraphGroupProvider(GraphServiceClient graphServiceClient)
        {
            _graphServiceClient = graphServiceClient;
        }

        public override async Task<Resource> CreateAsync(Resource resource, string correlationIdentifier)
        {
            if (resource.Identifier != null)
            {
                throw new HttpResponseException(HttpStatusCode.BadRequest);
            }

            Core2Group group = resource as Core2Group;

            if (string.IsNullOrWhiteSpace(group.DisplayName))
            {
                throw new HttpResponseException(HttpStatusCode.BadRequest);
            }

            var owners = group.Members
                .Where(a => string.Equals(a.TypeName, "owner", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!owners.Any())
            {
                //must be at least 1 member to become the group owner
                var respOwner = new HttpResponseMessage(HttpStatusCode.BadRequest);
                respOwner.ReasonPhrase = "At least one owner (member type: 'owner') must be sent to create a group";
                throw new HttpResponseException(respOwner);
            }

            var queryOptions = new List<QueryOption>()
            {
                new QueryOption("$count", "true"),
                new QueryOption("$search", $"\"displayName:{group.DisplayName}\"")
            };

            var groups = await _graphServiceClient.Groups
                .Request(queryOptions)
                .Header("ConsistencyLevel", "eventual")
                .Select("id,displayName")
                .GetAsync();

            if (groups.Any(a => string.Equals(a.DisplayName, group.DisplayName, StringComparison.Ordinal)))
            {
                throw new HttpResponseException(HttpStatusCode.Conflict);
            }

            string url = $"{_graphServiceClient.BaseUrl}/users/";
            var additionalData = new Dictionary<string, object>
                {
                    {"owners@odata.bind", new List<string>()}
                };
            //add owners
            foreach (var owner in owners)
            {
                (additionalData["owners@odata.bind"] as List<string>).Add(url + owner.Value);
            }

            var graphGroup = new Graph.Group
            {
                DisplayName = group.DisplayName,
                MailNickname = group.DisplayName,
                MailEnabled = false,
                SecurityEnabled = true,
                GroupTypes = new List<string>() { },
                AdditionalData = additionalData
            };

            var resp = await _graphServiceClient.Groups
                .Request()
                .AddAsync(graphGroup);

            resource.Identifier = resp.Id;

            return resource;
        }

        public override async Task DeleteAsync(IResourceIdentifier resourceIdentifier, string correlationIdentifier)
        {
            if (string.IsNullOrWhiteSpace(resourceIdentifier?.Identifier))
            {
                throw new HttpResponseException(HttpStatusCode.BadRequest);
            }

            string identifier = resourceIdentifier.Identifier;

            await _graphServiceClient.Groups[identifier]
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
            var group = await _graphServiceClient.Groups[identifier]
               .Request()
               .Expand("Members")
               .GetAsync();
            var c2Group = new Core2Group
            {
                DisplayName = group.DisplayName,
                Identifier = group.Id

            };
            c2Group.Metadata.Created = group.CreatedDateTime.HasValue ? group.CreatedDateTime.Value.DateTime : DateTime.MinValue;

            if (group.Members != null)
            {
                var members = new List<Member>();
                var existingMembers = group.Members.ToList();
                foreach (var member in existingMembers)
                {
                    members.Add(new Member { TypeName = member.ODataType, Value = member.Id });
                }
                c2Group.Members = members;
            }
            return c2Group;
        }

        public override async Task UpdateAsync(IPatch patch, string correlationIdentifier)
        {
            if (null == patch)
            {
                throw new ArgumentNullException(nameof(patch));
            }

            if (null == patch.ResourceIdentifier)
            {
                throw new ArgumentException(SystemForCrossDomainIdentityManagementServiceResources.ExceptionInvalidOperation);
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

            var group = new Graph.Group
            {
                Id = patch.ResourceIdentifier.Identifier
            };

            //load members for comflict comparison later
            var groupMembers = await _graphServiceClient.Groups[patch.ResourceIdentifier.Identifier].Members
              .Request()
              .GetAsync();
            group.Members = groupMembers;

            await Apply(group, patchRequest, _graphServiceClient);

            group.Members = null; //clear members before update since that data will be in AdditionalData

            await _graphServiceClient.Groups[patch.ResourceIdentifier.Identifier]
                .Request()
                .UpdateAsync(group);

        }

        private static async Task Apply(Group group, PatchRequest2 patch, GraphServiceClient graphServiceClient)
        {
            if (null == group)
            {
                throw new ArgumentNullException(nameof(group));
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

                OperationValue[] values = null;
                if (operation?.Value != null)
                {
                    values =
                    JsonConvert.DeserializeObject<OperationValue[]>(
                        operation.Value,
                        ProtocolConstants.JsonSettings.Value);
                }

                if (values == null)
                {
                    string value = null;
                    if (operation?.Value != null)
                    {
                        value = JsonConvert.DeserializeObject<string>(operation.Value, ProtocolConstants.JsonSettings.Value);
                    }

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

                await Apply(group, operationInternal, graphServiceClient);
            }
        }

        private static async Task Apply(Group group, PatchOperation2 operation, GraphServiceClient graphServiceClient)
        {
            if (null == operation || null == operation.Path || string.IsNullOrWhiteSpace(operation.Path.AttributePath))
            {
                return;
            }

            OperationValue value;
            switch (operation.Path.AttributePath)
            {
                case AttributeNames.DisplayName:
                    value = operation.Value.SingleOrDefault();

                    if (OperationName.Remove != operation.Name)
                    {
                        group.DisplayName = value.Value;
                    }
                    break;

                case AttributeNames.Members:
                    if (operation.Value != null)
                    {
                        switch (operation.Name)
                        {
                            case OperationName.Add:
                                IEnumerable<Member> membersToAdd =
                                     operation
                                     .Value
                                     .Select((OperationValue item) => new Member() { Value = item.Value })
                                     .ToArray();

                                string url = $"{graphServiceClient.BaseUrl}/directoryObjects/";
                                var additionalData = new Dictionary<string, object>
                                {
                                    {"members@odata.bind", new List<string>()}
                                };

                                var existingMembers = group.Members.ToList();
                                foreach (var member in membersToAdd)
                                {
                                    if (!existingMembers.Any(a => a.Id == member.Value))
                                    {
                                        (additionalData["members@odata.bind"] as List<string>).Add(url + member.Value);
                                    }
                                }

                                group.AdditionalData = additionalData;

                                break;

                            case OperationName.Remove:
                                if (null == group.Members)
                                {
                                    break;
                                }

                                if (operation?.Value?.FirstOrDefault()?.Value == null)
                                {
                                    //no users to remove
                                    break;
                                }
                                IEnumerable<Member> membersToRemove =
                                    operation
                                    .Value
                                    .Select((OperationValue item) => new Member() { Value = item.Value })
                                    .ToArray();

                                foreach (var member in membersToRemove)
                                {
                                    await graphServiceClient.Groups[group.Id].Members[member.Value].Reference
                                       .Request()
                                       .DeleteAsync();
                                }

                                break;
                        }
                    }
                    break;
            }
        }
    }
}
