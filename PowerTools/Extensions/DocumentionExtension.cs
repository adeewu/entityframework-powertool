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
    public class DocumentionExtension
    {
        public List<ColumnModel> GenerateDocumentation(EntityType[] entityTypes, ConnectionStringSettings connectionStringSettings)
        {
            var columnModels = getColumnDocument(entityTypes.Select(p => "'" + p.Name + "'"), connectionStringSettings);
            foreach (var entityType in entityTypes)
            {
                foreach (var property in entityType.Properties)
                {
                    var columnModel = columnModels.FirstOrDefault(p => p.Name == property.Name && p.TableName == entityType.Name);
                    if (columnModel != null) continue;

                    columnModels.Add(new ColumnModel
                    {
                        Name = property.Name,
                        TableName = entityType.Name,
                        Description = property.Name,
                    });
                }
            }

            return columnModels;
        }

        private List<ColumnModel> getColumnDocument(IEnumerable<string> tableNames, ConnectionStringSettings connectionStringSettings)
        {
            var sql = string.Format("SELECT  c.object_id AS ID ,c.name AS Name ,ta.name AS TableName ,(SELECT value FROM sys.extended_properties AS ex WHERE     ex.major_id = c.object_id AND ex.minor_id = c.column_id ) AS [Description] FROM sys.columns AS c INNER JOIN sys.tables AS ta ON c.object_id = ta.object_id WHERE ta.name IN ({0}) ORDER BY ta.name,c.column_id", string.Join(",", tableNames));

            using (var connection = new SqlConnection(connectionStringSettings.ConnectionString))
            {
                using (var adapter = new SqlDataAdapter(sql, connection))
                {
                    using (var set = new DataSet())
                    {
                        adapter.Fill(set);
                        return set.Tables[0].Rows.Cast<DataRow>().Select(p => new ColumnModel
                        {
                            ID = Convert.ToInt32(p["ID"]),
                            Name = p["Name"].ToString(),
                            TableName = p["TableName"].ToString(),
                            Description = p["Description"].ToString().Replace(Environment.NewLine, Environment.NewLine + "///").Replace("<", "&lt;").Replace(">", "&tt;"),
                        }).ToList();

                    }
                }
            }
        }
    }

    public class ColumnModel
    {
        public int ID { get; set; }
        public string Name { get; set; }
        public string TableName { get; set; }
        public string Description { get; set; }
    }
}
