using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Storage;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    public Operation StartCatalogBackup(string key)
    {
        const string scope="catalog-maintenance";
        return Start("catalog_backup",new(scope,0,0,scope,"workspace","local"),key,Wire.Hash(new { action="catalog_backup",schema=Catalog.CurrentSchemaVersion }),
            async(operation,cancellation)=>
            {
                cancellation.ThrowIfCancellationRequested();
                var backup=Catalog.CreateBackup(); Catalog.VerifyBackup(backup);
                // Native SQLite backup runs to quiescence; cancellation is checked before publishing its result.
                cancellation.ThrowIfCancellationRequested();
                Catalog.Put(backup.Id,"catalog_backup",backup); Catalog.AppendAudit("catalog_backup_created",backup.Id,backup.Hash);
                Catalog.Transition(operation.Id,OperationState.Completed,backup.Id); await Task.CompletedTask; return true;
            });
    }
    public CatalogBackup VerifyCatalogBackup(string id)
    {
        var backup=Record<CatalogBackup>(id,"catalog_backup"); Catalog.VerifyBackup(backup); return backup;
    }
}
