// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.
namespace Microsoft.DbContextPackage.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Data.Common;
    using System.Data.Entity.Design;
    using System.Data.Entity.Design.PluralizationServices;
    using System.Data.Metadata.Edm;
    using System.Data.SqlClient;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Xml;
    using Microsoft.DbContextPackage.Extensions;
    using Microsoft.DbContextPackage.Resources;
    using Microsoft.DbContextPackage.Utilities;
    using Microsoft.VisualStudio.Data.Core;
    using Microsoft.VisualStudio.Data.Services;
    using Microsoft.VisualStudio.Shell;
    using Project = EnvDTE.Project;

    internal class ReverseEngineerCodeFirstHandler
    {
        private readonly DbContextPackage package;
        private Project targetProject;

        public ReverseEngineerCodeFirstHandler(DbContextPackage package)
        {
            DebugCheck.NotNull(package);

            this.package = package;
        }

        public void ReverseEngineerCodeFirstMain(Project project)
        {
            DebugCheck.NotNull(project);
            targetProject = project;

            try
            {
                var startTime = DateTime.Now;

                var _storeMetadataFilters = new List<EntityStoreSchemaFilterEntry>
                {
                    new EntityStoreSchemaFilterEntry(null, null, "EdmMetadata", EntityStoreSchemaFilterObjectTypes.Table, EntityStoreSchemaFilterEffect.Exclude),
                    new EntityStoreSchemaFilterEntry(null, null, "__MigrationHistory", EntityStoreSchemaFilterObjectTypes.Table, EntityStoreSchemaFilterEffect.Exclude),
                    new EntityStoreSchemaFilterEntry(null, null, "sysdiagrams", EntityStoreSchemaFilterObjectTypes.Table, EntityStoreSchemaFilterEffect.Exclude),
                    //new EntityStoreSchemaFilterEntry(null, null, null, EntityStoreSchemaFilterObjectTypes.View, EntityStoreSchemaFilterEffect.Exclude),
                    new EntityStoreSchemaFilterEntry(null, null, null, EntityStoreSchemaFilterObjectTypes.Function, EntityStoreSchemaFilterEffect.Exclude)
                };

                //build.txt不存在或者空文本生成所有实体，有文本按设置生成
                var buildFilename = Path.Combine(targetProject.GetProjectDir(), "build.txt");
                if (File.Exists(buildFilename))
                {
                    var content = File.ReadAllText(buildFilename);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        var allowFilterEntrys = content.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => new EntityStoreSchemaFilterEntry(null, null, p, EntityStoreSchemaFilterObjectTypes.Table, EntityStoreSchemaFilterEffect.Allow));

                        _storeMetadataFilters.AddRange(allowFilterEntrys);
                    }
                }

                var connectionStringSettings = DoConnectionStringSettings(targetProject);
                var entityFrameworkVersion = GetEntityFrameworkVersion(package, targetProject);

                // Load store schema
                var storeGenerator = new EntityStoreSchemaGenerator(connectionStringSettings.ProviderName, connectionStringSettings.ConnectionString, "dbo");
                storeGenerator.GenerateForeignKeyProperties = true;
                var errors = storeGenerator.GenerateStoreMetadata(_storeMetadataFilters).Where(e => e.Severity == EdmSchemaErrorSeverity.Error);
                errors.HandleErrors(Strings.ReverseEngineer_SchemaError);

                if (storeGenerator.EntityContainer.BaseEntitySets == null || storeGenerator.EntityContainer.BaseEntitySets.Count() == 0)
                {
                    package.LogError("生成中断，没有找到对应的表", new Exception("生成中断，没有找到对应的表"));
                    return;
                }

                // Generate default mapping
                package.LogInfo(Strings.ReverseEngineer_GenerateMapping);
                var modelGenerator = new EntityModelSchemaGenerator(storeGenerator.EntityContainer, "DefaultNamespace", connectionStringSettings.Name);
                modelGenerator.PluralizationService = PluralizationService.CreateService(new CultureInfo("en"));
                modelGenerator.GenerateForeignKeyProperties = true;
                modelGenerator.GenerateMetadata();

                var entityTypes = modelGenerator.EdmItemCollection.OfType<EntityType>().ToArray();
                EdmPropertyExtension.ColumnModels = new DocumentionExtension().GenerateDocumentation(entityTypes, connectionStringSettings);
                EntityTypeExtension.GetTableDescriptions(connectionStringSettings);

                var solutionProjects = targetProject.DTE.Solution.Projects.OfType<Project>();

                Action<IEnumerable<Project>> loopProjectsAction = null;
                loopProjectsAction = projects =>
                {
                    foreach (var item in projects)
                    {
                        if (item.FullName.EndsWith(".csproj"))
                        {
                            ReverseEngineerCodeFirst(item, modelGenerator, entityTypes, storeGenerator.StoreItemCollection, entityFrameworkVersion);
                            continue;
                        }

                        if (item.ProjectItems.Count > 0)
                        {
                            var subProjects = new List<Project>();
                            foreach (EnvDTE.ProjectItem subProject in item.ProjectItems)
                            {
                                subProjects.Add(subProject.SubProject);
                            }

                            loopProjectsAction(subProjects);
                        }
                    }
                };
                loopProjectsAction(solutionProjects);

                var duration = DateTime.Now - startTime;
                package.LogInfo(Strings.ReverseEngineer_Complete(duration.ToString(@"h\:mm\:ss")));
            }
            catch (Exception exception)
            {
                package.LogError(exception.Message, exception);
            }
        }

        public void ReverseEngineerCodeFirst(Project project, EntityModelSchemaGenerator modelGenerator, EntityType[] entityTypes, StoreItemCollection StoreItemCollection, Version entityFrameworkVersion)
        {
            DebugCheck.NotNull(project);

            try
            {
                var mappings = new EdmMapping(modelGenerator, StoreItemCollection);
                var projectNamespace = (string)project.Properties.Item("RootNamespace").Value;

                var reverseEngineerCodeFirstFolder = new DirectoryInfo(Path.Combine(project.GetProjectDir(), "CodeTemplates", "ReverseEngineerCodeFirst"));
                if (!reverseEngineerCodeFirstFolder.Exists) return;

                var t4Files = reverseEngineerCodeFirstFolder.GetFiles().Where(p => p.Extension == ".tt");
                if (!t4Files.Any()) return;

                // Generate Entity Classes and Mappings
                var templateProcessor = new TemplateProcessor(project);
                foreach (var file in t4Files)
                {
                    if (file.Length == 0) continue;

                    #region 特殊处理Context
                    if (file.Name.ToLower().EndsWith("context.tt"))
                    {
                        package.LogInfo("Context");

                        var contextHost = new EfTextTemplateHost
                        {
                            EntityContainer = modelGenerator.EntityContainer,
                            Namespace = projectNamespace,
                            EntityFrameworkVersion = entityFrameworkVersion
                        };

                        SaveProjectFile(project, contextHost, templateProcessor, modelGenerator.EntityContainer.Name.Replace("Context", ""), "Models", "Context.tt");
                        continue;
                    }
                    #endregion

                    #region 实体处理
                    var fileName = file.Name.Replace(".tt", "");
                    fileName = fileName.EndsWith("s") ? fileName : fileName + "s";

                    foreach (var entityType in entityTypes)
                    {
                        package.LogInfo(file.FullName + "|" + entityType.Name);

                        var efHost = new EfTextTemplateHost
                        {
                            EntityType = entityType,
                            EntityContainer = modelGenerator.EntityContainer,
                            Namespace = projectNamespace,
                            EntityFrameworkVersion = entityFrameworkVersion,
                            TableSet = mappings.EntityMappings[entityType].Item1,
                            PropertyToColumnMappings = mappings.EntityMappings[entityType].Item2,
                            ManyToManyMappings = mappings.ManyToManyMappings
                        };

                        SaveProjectFile(project, efHost, templateProcessor, entityType.Name, fileName, file.Name);
                    }
                    #endregion
                }
            }
            catch (Exception exception)
            {
                package.LogError(project.Name + "生成出错", exception);
            }
        }

        #region connectionString
        private ConnectionStringSettings DoConnectionStringSettings(Project project)
        {
            var connectionStringSettings = GetConnectionStringSettingsFromConfigFile(project);
            if (connectionStringSettings == null)
            {
                // Show dialog with SqlClient selected by default
                var dialogFactory = package.GetService<IVsDataConnectionDialogFactory>();
                var dialog = dialogFactory.CreateConnectionDialog();
                dialog.AddAllSources();
                dialog.SelectedSource = new Guid("067ea0d9-ba62-43f7-9106-34930c60c528");
                var dialogResult = dialog.ShowDialog(connect: true);

                if (dialogResult == null)
                {
                    throw new NullReferenceException("请填写数据库信息");
                }

                // Find connection string and provider
                package.LogInfo(Strings.ReverseEngineer_LoadSchema);
                var connection = (DbConnection)dialogResult.GetLockedProviderObject();
                var connectionString = connection.ConnectionString;
                var providerManager = (IVsDataProviderManager)Package.GetGlobalService(typeof(IVsDataProviderManager));
                IVsDataProvider dp;
                providerManager.Providers.TryGetValue(dialogResult.Provider, out dp);
                var providerInvariant = (string)dp.GetProperty("InvariantName");
                var contextName = connection.Database.Replace(" ", string.Empty).Replace(".", string.Empty).Replace("_", string.Empty) + "Context";

                connectionStringSettings = new ConnectionStringSettings(contextName, connection.ConnectionString, providerInvariant);

                AddConnectionStringToConfigFile(project, connectionStringSettings.ConnectionString, connectionStringSettings.ProviderName, connectionStringSettings.Name);
            }
            else
            {
                package.LogInfo("从配置文件中获取到连接信息");
            }

            if (!connectionStringSettings.Name.ToLower().EndsWith("context"))
            {
                connectionStringSettings.Name += connectionStringSettings.Name + "Context";
            }

            return connectionStringSettings;
        }

        private void AddConnectionStringToConfigFile(Project project, string connectionString, string providerInvariant, string connectionStringName)
        {
            DebugCheck.NotNull(project);
            DebugCheck.NotEmpty(providerInvariant);
            DebugCheck.NotEmpty(connectionStringName);

            // Find App.config or Web.config
            var configFilePath = Path.Combine(
                project.GetProjectDir(),
                project.IsWebProject()
                    ? "Web.config"
                    : "App.config");

            // Either load up the existing file or create a blank file
            var config = ConfigurationManager.OpenMappedExeConfiguration(
                new ExeConfigurationFileMap { ExeConfigFilename = configFilePath },
                ConfigurationUserLevel.None);

            // Find or create the connectionStrings section
            var connectionStringSettings = config.ConnectionStrings
                .ConnectionStrings
                .Cast<ConnectionStringSettings>()
                .FirstOrDefault(css => css.Name == connectionStringName);

            if (connectionStringSettings == null)
            {
                connectionStringSettings = new ConnectionStringSettings
                {
                    Name = connectionStringName
                };

                config.ConnectionStrings
                    .ConnectionStrings
                    .Add(connectionStringSettings);
            }

            // Add in the new connection string
            connectionStringSettings.ProviderName = providerInvariant;
            connectionStringSettings.ConnectionString = FixUpConnectionString(connectionString, providerInvariant);

            project.DTE.SourceControl.CheckOutItemIfNeeded(configFilePath);
            config.Save();

            // Add any new file to the project
            project.ProjectItems.AddFromFile(configFilePath);
        }

        private ConnectionStringSettings GetConnectionStringSettingsFromConfigFile(Project project)
        {
            // Find App.config or Web.config
            var configFilePath = Path.Combine(
                project.GetProjectDir(),
                project.IsWebProject()
                    ? "Web.config"
                    : "App.config");

            // Either load up the existing file or create a blank file
            var connectionStringSettings = ConfigurationManager
                .OpenMappedExeConfiguration(new ExeConfigurationFileMap { ExeConfigFilename = configFilePath }, ConfigurationUserLevel.None)
                .ConnectionStrings
                .ConnectionStrings
                .Cast<ConnectionStringSettings>()
                .FirstOrDefault(p => p.Name.ToLower().EndsWith("context"));

            return connectionStringSettings;
        }

        private string FixUpConnectionString(string connectionString, string providerName)
        {
            DebugCheck.NotEmpty(providerName);

            if (providerName != "System.Data.SqlClient")
            {
                return connectionString;
            }

            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                MultipleActiveResultSets = true
            };
            builder.Remove("Pooling");

            return builder.ToString();
        }
        #endregion

        private Version GetEntityFrameworkVersion(DbContextPackage package, Project project)
        {
            var vsProject = (VSLangProj.VSProject)project.Object;
            var entityFrameworkReference = vsProject.References.Cast<VSLangProj.Reference>().FirstOrDefault(r => r.Name == "EntityFramework");
            if (entityFrameworkReference == null)
            {
                // Add EF References
                package.LogInfo(Strings.ReverseEngineer_InstallEntityFramework);

                try
                {
                    project.InstallPackage("EntityFramework");
                }
                catch (Exception ex)
                {
                    entityFrameworkReference = vsProject.References.Cast<VSLangProj.Reference>().FirstOrDefault(r => r.Name == "EntityFramework");
                    if (entityFrameworkReference == null)
                    {
                        package.LogError(Strings.ReverseEngineer_InstallEntityFrameworkError, ex);
                        throw new Exception("安装EF出错！");
                    }
                }
            }

            return new Version(entityFrameworkReference.Version);
        }

        private string GetModelSavePath(string projectDir, string nameSpaceSuffix)
        {
            return Path.Combine(projectDir, Path.Combine(nameSpaceSuffix.Split(new char[] { '.' }, StringSplitOptions.RemoveEmptyEntries)));
        }

        private void SaveProjectFile(Project project, EfTextTemplateHost efHost, TemplateProcessor templateProcessor, string entityName, string defaultNameSpaceSuffix, string templateName)
        {
            var projectRoot = new FileInfo(project.FullName).Directory.FullName;

            var contextContents = templateProcessor.Process($"{projectRoot}\\CodeTemplates\\ReverseEngineerCodeFirst\\" + templateName, efHost);

            if (string.IsNullOrWhiteSpace(efHost.NameSpaceSuffix))
            {
                efHost.NameSpaceSuffix = defaultNameSpaceSuffix;
            }

            var modelsDirectory = GetModelSavePath(project.GetProjectDir(), efHost.NameSpaceSuffix);
            var modelFileName = Path.Combine(modelsDirectory, entityName + efHost.NameSuffix + efHost.FileExtension);

            project.AddNewFile(modelFileName, contextContents);
        }

        private class EdmMapping
        {
            public EdmMapping(EntityModelSchemaGenerator mcGenerator, StoreItemCollection store)
            {
                DebugCheck.NotNull(mcGenerator);
                DebugCheck.NotNull(store);

                // Pull mapping xml out
                var mappingDoc = new XmlDocument();
                var mappingXml = new StringBuilder();

                using (var textWriter = new StringWriter(mappingXml))
                {
                    mcGenerator.WriteStorageMapping(new XmlTextWriter(textWriter));
                }

                mappingDoc.LoadXml(mappingXml.ToString());

                var entitySets = mcGenerator.EntityContainer.BaseEntitySets.OfType<EntitySet>();
                var associationSets = mcGenerator.EntityContainer.BaseEntitySets.OfType<AssociationSet>();
                var tableSets = store.GetItems<EntityContainer>().Single().BaseEntitySets.OfType<EntitySet>();

                this.EntityMappings = BuildEntityMappings(mappingDoc, entitySets, tableSets);
                this.ManyToManyMappings = BuildManyToManyMappings(mappingDoc, associationSets, tableSets);
            }

            public Dictionary<EntityType, Tuple<EntitySet, Dictionary<EdmProperty, EdmProperty>>> EntityMappings { get; set; }

            public Dictionary<AssociationType, Tuple<EntitySet, Dictionary<RelationshipEndMember, Dictionary<EdmMember, string>>>> ManyToManyMappings { get; set; }

            private static Dictionary<AssociationType, Tuple<EntitySet, Dictionary<RelationshipEndMember, Dictionary<EdmMember, string>>>> BuildManyToManyMappings(XmlDocument mappingDoc, IEnumerable<AssociationSet> associationSets, IEnumerable<EntitySet> tableSets)
            {
                DebugCheck.NotNull(mappingDoc);
                DebugCheck.NotNull(associationSets);
                DebugCheck.NotNull(tableSets);

                // Build mapping for each association
                var mappings = new Dictionary<AssociationType, Tuple<EntitySet, Dictionary<RelationshipEndMember, Dictionary<EdmMember, string>>>>();
                var namespaceManager = new XmlNamespaceManager(mappingDoc.NameTable);
                namespaceManager.AddNamespace("ef", mappingDoc.ChildNodes[0].NamespaceURI);
                foreach (var associationSet in associationSets.Where(a => !a.ElementType.AssociationEndMembers.Where(e => e.RelationshipMultiplicity != RelationshipMultiplicity.Many).Any()))
                {
                    var setMapping = mappingDoc.SelectSingleNode(string.Format("//ef:AssociationSetMapping[@Name=\"{0}\"]", associationSet.Name), namespaceManager);
                    var tableName = setMapping.Attributes["StoreEntitySet"].Value;
                    var tableSet = tableSets.Single(s => s.Name == tableName);

                    var endMappings = new Dictionary<RelationshipEndMember, Dictionary<EdmMember, string>>();
                    foreach (var end in associationSet.AssociationSetEnds)
                    {
                        var propertyToColumnMappings = new Dictionary<EdmMember, string>();
                        var endMapping = setMapping.SelectSingleNode(string.Format("./ef:EndProperty[@Name=\"{0}\"]", end.Name), namespaceManager);
                        foreach (XmlNode fk in endMapping.ChildNodes)
                        {
                            var propertyName = fk.Attributes["Name"].Value;
                            var property = end.EntitySet.ElementType.Properties[propertyName];
                            var columnName = fk.Attributes["ColumnName"].Value;
                            propertyToColumnMappings.Add(property, columnName);
                        }

                        endMappings.Add(end.CorrespondingAssociationEndMember, propertyToColumnMappings);
                    }

                    mappings.Add(associationSet.ElementType, Tuple.Create(tableSet, endMappings));
                }

                return mappings;
            }

            private static Dictionary<EntityType, Tuple<EntitySet, Dictionary<EdmProperty, EdmProperty>>> BuildEntityMappings(XmlDocument mappingDoc, IEnumerable<EntitySet> entitySets, IEnumerable<EntitySet> tableSets)
            {
                DebugCheck.NotNull(mappingDoc);
                DebugCheck.NotNull(entitySets);
                DebugCheck.NotNull(tableSets);

                // Build mapping for each type
                var mappings = new Dictionary<EntityType, Tuple<EntitySet, Dictionary<EdmProperty, EdmProperty>>>();
                var namespaceManager = new XmlNamespaceManager(mappingDoc.NameTable);
                namespaceManager.AddNamespace("ef", mappingDoc.ChildNodes[0].NamespaceURI);
                foreach (var entitySet in entitySets)
                {
                    // Post VS2010 builds use a different structure for mapping
                    var setMapping = mappingDoc.ChildNodes[0].NamespaceURI == "http://schemas.microsoft.com/ado/2009/11/mapping/cs"
                        ? mappingDoc.SelectSingleNode(string.Format("//ef:EntitySetMapping[@Name=\"{0}\"]/ef:EntityTypeMapping/ef:MappingFragment", entitySet.Name), namespaceManager)
                        : mappingDoc.SelectSingleNode(string.Format("//ef:EntitySetMapping[@Name=\"{0}\"]", entitySet.Name), namespaceManager);

                    var tableName = setMapping.Attributes["StoreEntitySet"].Value;
                    var tableSet = tableSets.Single(s => s.Name == tableName);

                    var propertyMappings = new Dictionary<EdmProperty, EdmProperty>();
                    foreach (var prop in entitySet.ElementType.Properties)
                    {
                        var propMapping = setMapping.SelectSingleNode(string.Format("./ef:ScalarProperty[@Name=\"{0}\"]", prop.Name), namespaceManager);
                        var columnName = propMapping.Attributes["ColumnName"].Value;
                        var columnProp = tableSet.ElementType.Properties[columnName];

                        propertyMappings.Add(prop, columnProp);
                    }

                    mappings.Add(entitySet.ElementType, Tuple.Create(tableSet, propertyMappings));
                }

                return mappings;
            }
        }
    }
}
