// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.
namespace Microsoft.DbContextPackage.Utilities
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using EnvDTE;
    using Microsoft.DbContextPackage.Extensions;
    using Microsoft.DbContextPackage.Resources;
    using Microsoft.VisualStudio.Shell;
    using Microsoft.VisualStudio.TextTemplating;
    using Microsoft.VisualStudio.TextTemplating.VSHost;

    internal class TemplateProcessor
    {
        private readonly Project _project;
        private readonly IDictionary<string, string> _templateCache;

        public TemplateProcessor(Project project)
        {
            DebugCheck.NotNull(project);

            _project = project;
            _templateCache = new Dictionary<string, string>();
        }

        public string Process(string templatePath, EfTextTemplateHost host)
        {
            DebugCheck.NotEmpty(templatePath);
            DebugCheck.NotNull(host);

            host.TemplateFile = templatePath;

            var engine = GetEngine();
            var template = GetTemplate(templatePath);
            var output = engine.ProcessTemplate(template, host);

            var templateFilename = Path.GetFileName(templatePath);
            host.Errors.HandleErrors(Strings.ProcessTemplateError(templateFilename));

            return output;
        }

        private string GetTemplate(string templatePath)
        {
            DebugCheck.NotEmpty(templatePath);
            
            if (_templateCache.ContainsKey(templatePath))
            {
                return _templateCache[templatePath];
            }

            var filename = Path.Combine(_project.GetProjectDir(), templatePath);
            if (!File.Exists(filename))
            {
                throw new FileLoadException(string.Format("模板文件（{0}）不存在", filename));
            }

            string contents = File.ReadAllText(filename);
            if (string.IsNullOrWhiteSpace(contents))
            {
                throw new FileLoadException(string.Format("模板文件（{0}）内容为空", filename));
            }

            _templateCache.Add(templatePath, contents);

            return contents;
        }

        private static ITextTemplatingEngine GetEngine()
        {
            var textTemplating = (ITextTemplatingComponents)Package.GetGlobalService(typeof(STextTemplating));

            return textTemplating.Engine;
        }
    }
}
