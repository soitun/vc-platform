using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web.Http.Description;
using Swashbuckle.Swagger;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.Platform.Web.Swagger
{
    public class PolymorphismDocumentFilter : IDocumentFilter
    {
        private readonly bool _useFullTypeNames;

        public PolymorphismDocumentFilter(bool useFullTypeNames)
        {
            _useFullTypeNames = useFullTypeNames;
        }

        [CLSCompliant(false)]
        public void Apply(SwaggerDocument swaggerDoc, SchemaRegistry schemaRegistry, IApiExplorer apiExplorer)
        {
            RegisterSubClasses(schemaRegistry, apiExplorer);
        }

        private void RegisterSubClasses(SchemaRegistry schemaRegistry, IApiExplorer apiExplorer)
        {
            foreach (var type in apiExplorer.ApiDescriptions.Select(x => x.ResponseType()).Where(x => x != null).Distinct())
            {
                var schema = GetTypeSchema(schemaRegistry, type, false);

                // IApiExplorer contains types from all api controllers, so some of them could be not presented in specific module schemaRegistry.
                if (schema != null)
                {
                    // Find if type is registered in AbstractTypeFactory with descendants and TypeInfo have Discriminator filled
                    var abstractTypeFactory = typeof(AbstractTypeFactory<>).MakeGenericType(type);
                    var subtypesPropertyGetter = abstractTypeFactory.GetProperty("AllTypes", BindingFlags.Static | BindingFlags.Public).GetGetMethod();
                    var subtypes = (subtypesPropertyGetter.Invoke(abstractTypeFactory, null) as IEnumerable<Type>).ToArray();
                    var discriminatorPropertyGetter = abstractTypeFactory.GetProperty("Discriminator", BindingFlags.Static | BindingFlags.Public).GetGetMethod();
                    var discriminator = discriminatorPropertyGetter.Invoke(abstractTypeFactory, null) as string;

                    // Polymorphism registration required if we have at least one TypeInfo in AbstractTypeFactory and Discriminator is set
                    if (subtypes.Length > 0 && !string.IsNullOrEmpty(discriminator))
                    {
                        foreach (var subtype in subtypes)
                        {
                            var subtypeSchema = GetTypeSchema(schemaRegistry, subtype, false);

                            // Make sure all derivedTypes are in schemaRegistry
                            if (subtypeSchema == null)
                            {
                                schemaRegistry.GetOrRegister(subtype);
                                // Swashbuckle doesn't return some required information in Schema instance at GetOrRegister call
                                subtypeSchema = GetTypeSchema(schemaRegistry, subtype, false);
                            }

                            AddInheritanceToSubtypeSchema(schema, type, subtypeSchema);
                        }

                        AddDiscriminatorToBaseType(schema, discriminator);
                    }
                }
            }
        }

        private Schema GetTypeSchema(SchemaRegistry schemaRegistry, Type type, bool throwOnEmpty)
        {
            Schema result = null;
            var typeName = _useFullTypeNames ? type.FullName : type.FriendlyId();

            // IApiExplorer contains types from all api controllers, so some of them could be not presented in specific module schemaRegistry.
            if (schemaRegistry.Definitions.ContainsKey(typeName))
            {
                result = schemaRegistry.Definitions[typeName];
            }

            if (throwOnEmpty && result == null)
            {
                throw new KeyNotFoundException($"Subtype \"{type.FullName}\" does not exist in SchemaRegistry.");
            }

            return result;
        }

        private void AddDiscriminatorToBaseType(Schema baseTypeSchema, string discriminator)
        {
            // Need to make first discriminator character lower to avoid properties duplication because of case, as all properties in OpenApi spec are in camelCase
            discriminator = char.ToLowerInvariant(discriminator[0]) + discriminator.Substring(1);

            // Set up a discriminator property (it must be required)
            baseTypeSchema.discriminator = discriminator;
            baseTypeSchema.required = new List<string> { discriminator };

            if (!baseTypeSchema.properties.ContainsKey(discriminator))
            {
                baseTypeSchema.properties.Add(discriminator, new Schema { type = "string" });
            }
        }

        private void AddInheritanceToSubtypeSchema(Schema baseTypeSchema, Type baseType, Schema subtypeSchema)
        {
            var clonedSchema = new Schema
            {
                properties = subtypeSchema.properties.Where(x => !baseTypeSchema.properties.ContainsKey(x.Key)).ToDictionary(x => x.Key, x => x.Value),
                type = subtypeSchema.type,
                required = subtypeSchema.required
            };

            var baseTypeName = _useFullTypeNames ? baseType.FullName : baseType.FriendlyId();

            var parentSchema = new Schema { @ref = "#/definitions/" + baseTypeName };

            subtypeSchema.allOf = new List<Schema> { parentSchema, clonedSchema };

            //reset properties for they are included in allOf, should be null but code does not handle it
            subtypeSchema.properties = new Dictionary<string, Schema>();
        }
    }
}
