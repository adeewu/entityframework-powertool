using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace System.Data.Metadata.Edm
{
    public static class EdmPropertyExtension
    {
        public static List<ColumnModel> ColumnModels;

        public static string GetDescription(this EdmProperty property)
        {
            if (property.DeclaringType == null) return property.Name;

            var model = ColumnModels.FirstOrDefault(p => property.Name == p.Name && p.TableName == property.DeclaringType.Name);
            if (model == null) return property.Name;

            return model.Description;
        }
    }
}
