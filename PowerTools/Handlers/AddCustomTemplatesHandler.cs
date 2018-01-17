// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.
namespace Microsoft.DbContextPackage.Handlers
{
    using System;
    using System.IO;
    using System.Linq;
    using EnvDTE;
    using Microsoft.DbContextPackage.Extensions;
    using Microsoft.DbContextPackage.Resources;
    using Microsoft.DbContextPackage.Utilities;

    internal class AddCustomTemplatesHandler
    {
        private readonly DbContextPackage _package;

        public AddCustomTemplatesHandler(DbContextPackage package)
        {
            DebugCheck.NotNull(package);

            _package = package;
        }

        public void AddCustomTemplates(Project project)
        {
            DebugCheck.NotNull(project);

            try
            {
                AddTemplate(project);
            }
            catch (Exception ex)
            {
                _package.LogError(Strings.AddTemplatesError, ex);
            }
        }

        private static void AddTemplate(Project project)
        {
            DebugCheck.NotNull(project);

            var projectDir = project.GetProjectDir();

            var resourceFullNames = typeof(Templates).Assembly
                .GetManifestResourceNames()
                .Where(p => p.StartsWith("Microsoft.DbContextPackage.CodeTemplates.ReverseEngineerCodeFirst"));

            foreach (var resourceFullName in resourceFullNames)
            {
                var relativePath = resourceFullName.Replace("Microsoft.DbContextPackage.", "").Replace(".tt", "").Replace(".", "\\") + ".tt";

                var filePath = Path.Combine(projectDir, relativePath);
                var contents = Templates.GetDefaultTemplate(resourceFullName);
                var item = project.AddNewFile(filePath, contents);
                item.Properties.Item("CustomTool").Value = null;
            }
        }
    }
}
