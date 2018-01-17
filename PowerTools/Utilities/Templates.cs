// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.
namespace Microsoft.DbContextPackage.Utilities
{
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;

    internal static class Templates
    {
        public static string GetDefaultTemplate(string path)
        {
            DebugCheck.NotEmpty(path);
            
            var stream = typeof(Templates).Assembly.GetManifestResourceStream(path);
            Debug.Assert(stream != null);

            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
