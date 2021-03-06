﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SD.LLBLGen.Pro.ORMSupportClasses;
using SD.LLBLGen.Pro.QuerySpec;
using SD.LLBLGen.Pro.QuerySpec.Adapter;
using ServiceStack.CacheAccess;
using ServiceStack.ServiceHost;
using ServiceStack.Text;
using Northwind.Data.Dtos;
using Northwind.Data.EntityClasses;
using Northwind.Data.FactoryClasses;
using Northwind.Data.Helpers;
using Northwind.Data.HelperClasses;
using Northwind.Data.ServiceInterfaces;

namespace Northwind.Data.ServiceRepositories
{
    public abstract class EntityServiceRepositoryBase<TDto, TEntity, TEntityFactory> : IEntityServiceRepository<TDto>
        where TDto : CommonDtoBase
        where TEntity: CommonEntityBase, new() 
        where TEntityFactory : EntityFactoryBase2<TEntity>, new() 
    {
        public ICacheClient CacheClient { get; set; }
        public abstract IDataAccessAdapterFactory DataAccessAdapterFactory { get; set; }
        
        protected abstract EntityType EntityType { get; }
       
        internal virtual IDictionary< string, IEntityField2[] > UniqueConstraintMap
        {
            get { return new Dictionary< string, IEntityField2[] >(); }
            set { }
        }

        internal virtual IDictionary<string, IEntityField2> FieldMap
        {
            get { return RepositoryHelper.GetEntityTypeFieldMap(EntityType); }
            set { }
        }

        internal virtual IDictionary<string, IPrefetchPathElement2> IncludeMap
        {
            get { return RepositoryHelper.GetEntityTypeIncludeMap(EntityType); }
            set { }
        }

        internal virtual IDictionary<string, IEntityRelation> RelationMap
        {
            get { return RepositoryHelper.GetEntityTypeRelationMap(EntityType); }
            set { }
        }        
        
        #region Fetch Methods
        
        public EntityMetaDetailsResponse GetEntityMetaDetails(ServiceStack.ServiceInterface.Service service)
        {
            // The entity meta details don't change per entity type, so cache these for performance
            var cacheKey = string.Format("{0}-meta-details", EntityType.ToString().ToLower());
            var metaDetails = CacheClient.Get<EntityMetaDetailsResponse>(cacheKey);
            if (metaDetails != null)
                return metaDetails;
                
            var request = service.RequestContext.Get<IHttpRequest>();
            var appUri = request.GetApplicationUrl().TrimEnd('/');
            var baseServiceUri = appUri + request.PathInfo.Replace("/meta", "");
            var queryString = request.QueryString["format"] != null ? "&format=" + request.QueryString["format"] : "";
            var pkCount = FieldMap.Count(pk => pk.Value.IsPrimaryKey);
            var fields = new List<Link>();
            foreach (var f in FieldMap)
            {
                var isUnique = false;
                var ucs = UniqueConstraintMap.Where(uc => uc.Value.Any(x => x.Name.Equals(f.Key, StringComparison.InvariantCultureIgnoreCase))).ToArray();
                var link = Link.Create(
                  f.Key.ToCamelCase(), f.Value.DataType.Name, "field",
                  string.Format("{0}?select={1}{2}", baseServiceUri, f.Key.ToLowerInvariant(), queryString),
                  string.Format("The {0} field for the {1} resource.", f.Value.Name, typeof (TDto).Name),
                  new Dictionary<string, string>()
                );
                var props = new SortedDictionary<string, string>();
                props.Add("index", f.Value.FieldIndex.ToString(CultureInfo.InvariantCulture));
                if (f.Value.IsPrimaryKey)
                {
                    props.Add("isPrimaryKey", f.Value.IsPrimaryKey.ToString().ToLower());
                    if (pkCount == 1) isUnique = true;
                }
                if (f.Value.IsForeignKey)
                    props.Add("isForeignKey", "true");

                var ucNames = new List<string>();
                foreach (var uc in ucs)
                {
                    if (uc.Value.Count() == 1) isUnique = true;
                    ucNames.Add(uc.Key.ToLower());
                }
                if (ucNames.Any())
                    props.Add("partOfUniqueConstraints", string.Join(",", ucNames.ToArray()));
                if (isUnique)
                    props.Add("isUnique", "true");
                if (f.Value.IsOfEnumDataType)
                    props.Add("isOfEnumDataType", "true");
                if (f.Value.IsReadOnly)
                    props.Add("isReadOnly", "true");
                if (f.Value.IsNullable)
                    props.Add("isNullable", "true");
                if (f.Value.IsOfEnumDataType)
                    props.Add("isEnumType", "true");
                if (f.Value.MaxLength > 0)
                    props.Add("maxLength", f.Value.MaxLength.ToString(CultureInfo.InvariantCulture));
                if (f.Value.Precision > 0)
                    props.Add("precision", f.Value.Precision.ToString(CultureInfo.InvariantCulture));
                if (f.Value.Scale > 0)
                    props.Add("scale", f.Value.Scale.ToString(CultureInfo.InvariantCulture));
                link.Properties = new Dictionary<string, string>(props);
                fields.Add(link);
            }

            var includes = new List<Link>();
            foreach (var f in IncludeMap)
            {
                var relationType = "";
                switch (f.Value.TypeOfRelation)
                {
                    case RelationType.ManyToMany:
                        relationType = "n:n";
                        break;
                    case RelationType.ManyToOne:
                        relationType = "n:1";
                        break;
                    case RelationType.OneToMany:
                        relationType = "1:n";
                        break;
                    case RelationType.OneToOne:
                        relationType = "1:1";
                        break;
                }
                var relatedDtoContainerName =
                    (Enum.GetName(typeof (EntityType), f.Value.ToFetchEntityType) ?? "").Replace("Entity", "");
                var link = Link.Create(
                    f.Key.ToCamelCase(),
                    (relationType.EndsWith("n") ? relatedDtoContainerName + "Collection" : relatedDtoContainerName),
                    "include",
                    string.Format("{0}?include={1}{2}", baseServiceUri, f.Key.ToLowerInvariant(), queryString),
                    string.Format(
                        "The {0} field for the {1} resource to include in the results returned by a query.",
                        f.Value.PropertyName,
                        typeof (TDto).Name),
                    new Dictionary<string, string>
                        {
                            {"field", f.Value.PropertyName.ToCamelCase()},
                            {
                                "relatedType",
                                (Enum.GetName(typeof (EntityType), f.Value.ToFetchEntityType) ?? "").Replace("Entity", "")
                            },
                            {"relationType", relationType}
                        });
                includes.Add(link);
            }

            var relations = new List<Link>();
            foreach (var f in RelationMap)
            {
                var isPkSide = f.Value.StartEntityIsPkSide;
                var isFkSide = !f.Value.StartEntityIsPkSide;
                var pkFieldCore = f.Value.GetAllPKEntityFieldCoreObjects().FirstOrDefault();
                var fkFieldCore = f.Value.GetAllFKEntityFieldCoreObjects().FirstOrDefault();
                var thisField = isPkSide
                                    ? (pkFieldCore == null ? "" : pkFieldCore.Name)
                                    : (fkFieldCore == null ? "" : fkFieldCore.Name);
                var relatedField = isFkSide
                                    ? (pkFieldCore == null ? "" : pkFieldCore.Name)
                                    : (fkFieldCore == null ? "" : fkFieldCore.Name);
                var thisEntityAlias = isPkSide
                                    ? (pkFieldCore == null ? "": pkFieldCore.ActualContainingObjectName.Replace("Entity", ""))
                                    : (fkFieldCore == null ? "": fkFieldCore.ActualContainingObjectName.Replace("Entity", ""));
                var relatedEntityAlias = isFkSide
                                    ? (pkFieldCore == null ? "": pkFieldCore.ActualContainingObjectName.Replace("Entity", ""))
                                    : (fkFieldCore == null ? "": fkFieldCore.ActualContainingObjectName.Replace("Entity", ""));
                var relationType = "";
                switch (f.Value.TypeOfRelation)
                {
                    case RelationType.ManyToMany:
                        relationType = "n:n";
                        break;
                    case RelationType.ManyToOne:
                        relationType = "n:1";
                        break;
                    case RelationType.OneToMany:
                        relationType = "1:n";
                        break;
                    case RelationType.OneToOne:
                        relationType = "1:1";
                        break;
                }
                var link = Link.Create(
                  f.Key.ToCamelCase(),
                  relationType.EndsWith("n") ? relatedEntityAlias + "Collection" : relatedEntityAlias, "relation",
                  string.Format("{0}?relations={1}{2}", baseServiceUri, f.Key.ToLowerInvariant(), queryString),
                  string.Format(
                    "The relation '{0}' for the {1} resource between a {2} (PK) and a {3} (FK) resource.",
                    f.Value.MappedFieldName,
                    typeof (TDto).Name, f.Value.AliasStartEntity.Replace("Entity", ""),
                    f.Value.AliasEndEntity.Replace("Entity", "")),
                  new Dictionary<string, string>
                  {
                    {"field", f.Value.MappedFieldName.ToCamelCase()},
                    {"joinHint", f.Value.JoinType.ToString().ToLower()},
                    {"relationType", relationType},
                    {"isPkSide", isPkSide.ToString().ToLower()},
                    {"isFkSide", isFkSide.ToString().ToLower()},
                    {"isWeakRelation", f.Value.IsWeak.ToString().ToLower()},
                    {"pkTypeName", isPkSide ? thisEntityAlias : relatedEntityAlias},
                    {
                      "pkTypeField",
                      isPkSide ? thisField.ToCamelCase() : relatedField.ToCamelCase()
                    },
                    {"fkTypeName", isFkSide ? thisEntityAlias : relatedEntityAlias},
                    {
                      "fkTypeField",
                      isFkSide ? thisField.ToCamelCase() : relatedField.ToCamelCase()
                    },
                  });
                relations.Add(link);
                // add relation to fields list as well
                fields.Add(Link.Create(
                  f.Value.MappedFieldName.ToCamelCase(),
                  relationType.EndsWith("n") ? relatedEntityAlias + "Collection": relatedEntityAlias,
                  "field", null,
                  string.Format("The {0} field for the {1} resource.", f.Value.MappedFieldName,
                        typeof (TDto).Name), new Dictionary<string, string>
                          {
                            {"relation", f.Value.MappedFieldName.ToCamelCase()},
                            {"relationType", relationType},
                            {"isPkSide", isPkSide.ToString().ToLower()},
                            {"isFkSide", isFkSide.ToString().ToLower()},
                          }));
            }

            metaDetails = new EntityMetaDetailsResponse()
                {
                    Fields = fields.ToArray(),
                    Includes = includes.ToArray(),
                    Relations = relations.ToArray()
                };
            CacheClient.Set(cacheKey, metaDetails);
            return metaDetails;
        }

        internal EntityCollection<TEntity> Fetch(IDataAccessAdapter adapter, SortExpression sortExpression,
                                                 ExcludeIncludeFieldsList excludedIncludedFields,
                                                 IPrefetchPath2 prefetchPath, IRelationPredicateBucket predicateBucket,
                                                 int pageNumber,
                                                 int pageSize, int limit, out int totalItemCount)
        {
            return Fetch(adapter, sortExpression, excludedIncludedFields, prefetchPath, predicateBucket, pageNumber,
                         pageSize, limit, 0, out totalItemCount);
        }

        internal EntityCollection<TEntity> Fetch(IDataAccessAdapter adapter, SortExpression sortExpression,
                                                 ExcludeIncludeFieldsList excludedIncludedFields,
                                                 IPrefetchPath2 prefetchPath, IRelationPredicateBucket predicateBucket,
                                                 int pageNumber,
                                                 int pageSize, int limit, int cacheTimeInSeconds,
                                                 out int totalItemCount)
        {
            var qf = new QueryFactory();
            var q = qf.Create().Select(qf.Create<TEntity>().Where(predicateBucket.PredicateExpression).CountRow());

            if (cacheTimeInSeconds > 0)
                q = q.CacheResultset(cacheTimeInSeconds);

            totalItemCount = adapter.FetchScalar<int>(q);

            var entities = new EntityCollection<TEntity>(new TEntityFactory());
            var parameters = new QueryParameters
                {
                    CollectionToFetch = entities,
                    FilterToUse = predicateBucket.PredicateExpression,
                    RelationsToUse = predicateBucket.Relations,
                    SorterToUse = sortExpression,
                    ExcludedIncludedFields = excludedIncludedFields,
                    PrefetchPathToUse = prefetchPath,
                    CacheResultset = cacheTimeInSeconds > 0,
                    CacheDuration = cacheTimeInSeconds > 0
                                ? TimeSpan.FromSeconds(cacheTimeInSeconds)
                                : TimeSpan.Zero,
                    RowsToTake = limit > 0 ? limit : pageSize,
                    RowsToSkip = limit > 0 ? 0 : pageSize*(pageNumber - 1)
                };
            adapter.FetchEntityCollection(parameters);

            return entities;
        }

        internal TEntity Fetch(IDataAccessAdapter adapter, IPredicate predicate, IPrefetchPath2 prefetchPath, ExcludeIncludeFieldsList excludedIncludedFields, int cacheTimeInSeconds)
        {
            var qf = new QueryFactory();
            var q = qf.Create<TEntity>().Where(predicate);

            if(cacheTimeInSeconds > 0)
                q = q.CacheResultset(cacheTimeInSeconds);

            var results = adapter.FetchQuery(q);
            return (TEntity)(results[0]);
        }

        internal void FixupLimitAndPagingOnRequest(GetCollectionRequest request)
        {
            if (request.PageNumber > 0 || request.PageSize > 0)
                request.Limit = 0; // override the limit, paging takes precedence if specified

            if (request.PageNumber < 1) request.PageNumber = 1;
            if (request.PageSize < 1) request.PageSize = 10;
        }
        
        #endregion
    }
}
