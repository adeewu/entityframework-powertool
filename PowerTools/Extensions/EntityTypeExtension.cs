using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.Metadata.Edm;
using System.Data.SqlClient;
using System.Linq;
using System.Text;

namespace System.Data.Metadata.Edm
{
    public static class EntityTypeExtension
    {
        public static Dictionary<string, string> TableDescriptions;

        /// <summary>
        /// 获取表说明字典
        /// </summary>
        /// <param name="connectionStringSettings"></param>
        /// <returns></returns>
        public static void GetTableDescriptions(ConnectionStringSettings connectionStringSettings)
        {
            var sql = @"SELECT Name = case when a.colorder = 1 then d.name 
                                           else '' end, 
                               Description = case when a.colorder = 1 then isnull(f.value, '') 
                                             else '' end
                        FROM syscolumns a 
                               inner join sysobjects d 
                                  on a.id = d.id 
                                     and d.xtype = 'U' 
                                     and d.name <> 'sys.extended_properties'
                               left join sys.extended_properties   f 
                                 on a.id = f.major_id 
                                    and f.minor_id = 0
                        Where (case when a.colorder = 1 then d.name else '' end) <>''";

            using (var connection = new SqlConnection(connectionStringSettings.ConnectionString))
            {
                using (var adapter = new SqlDataAdapter(sql, connection))
                {
                    using (var set = new DataSet())
                    {
                        adapter.Fill(set);
                        TableDescriptions = set.Tables[0].Rows.Cast<DataRow>()
                            .Select(p => new
                            {
                                Name = p["Name"].ToString(),
                                Description = p["Description"].ToString().Replace(Environment.NewLine, Environment.NewLine + "///").Replace("<", "&lt;").Replace(">", "&tt;"),
                            })
                            .ToDictionary(p => p.Name, p => p.Description);
                    }
                }
            }
        }

        public static string GetDescription(this EntityType entityType)
        {
            if (entityType == null || TableDescriptions == null) return entityType.Name;

            return TableDescriptions.Where(p => p.Key == entityType.Name).Select(p => p.Value).FirstOrDefault();
        }
    }
}
