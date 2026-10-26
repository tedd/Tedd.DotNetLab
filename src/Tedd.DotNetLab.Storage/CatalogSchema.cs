using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tedd.DotNetLab.Contracts;
using Microsoft.Data.Sqlite;

namespace Tedd.DotNetLab.Storage;

public record CatalogBackup(string Id,string RelativePath,string Hash,long Length,int? SchemaVersion,DateTimeOffset CreatedAt);
public record CatalogCheckpoint(bool Busy,long LogFrames,long CheckpointedFrames,string Mode);

public static class CatalogMigrations
{
    public const int CurrentVersion=5;
    public static string Script(int version)
    {
        if(version is <1 or >CurrentVersion) throw new LabException("schema_unsupported","Unknown catalog migration.");
        using var stream=typeof(Catalog).Assembly.GetManifestResourceStream($"Tedd.DotNetLab.Storage.Migrations.{version:000}.sql")
            ??throw new InvalidOperationException("Packaged catalog migration is missing.");
        using var reader=new StreamReader(stream); return reader.ReadToEnd();
    }
    internal static Dictionary<string,string> Objects(int version)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        for(int step=1;step<=version;step++)
            foreach(string statement in Script(step).Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
            {
                var name=Regex.Match(statement,@"\ACREATE\s+(?:TABLE|INDEX)\s+([A-Za-z_]+)",RegexOptions.IgnoreCase);
                if(!name.Success&&statement.StartsWith("INSERT INTO ",StringComparison.Ordinal)) continue;
                if(!name.Success) throw new InvalidOperationException("Migration contains an unrecognized schema statement.");
                result.Add(name.Groups[1].Value,Normalize(statement));
            }
        return result;
    }
    internal static string Normalize(string sql)=>Regex.Replace(sql.Trim().TrimEnd(';'),@"\s+","").ToUpperInvariant().Replace("IFNOTEXISTS","",StringComparison.Ordinal);
}

public sealed partial class Catalog
{
    public const int CurrentSchemaVersion=CatalogMigrations.CurrentVersion;
    public CatalogBackup? MigrationBackup { get; private set; }
    private static void EnsureSafePath(string path)
    {
        string full=Path.GetFullPath(path);
        FileAttributes? attributes=null;
        try { attributes=File.GetAttributes(full); }
        catch(FileNotFoundException) { }
        catch(DirectoryNotFoundException) { }
        if(attributes is { } observed&&(observed&FileAttributes.ReparsePoint)!=0)
            throw new LabException("invalid_workspace","Catalog files cannot be reparse points.");
        for(var directory=new DirectoryInfo(Path.GetDirectoryName(full)!);directory is not null;directory=directory.Parent)
            if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new LabException("invalid_workspace","Catalog paths cannot traverse reparse points.");
    }
    private void EnsureSafeCatalogFiles()
    {
        string path=Path.Combine(Root,"catalog.sqlite");
        foreach(string suffix in new[]{"","-wal","-shm","-journal"}) EnsureSafePath(path+suffix);
    }
    private SqliteConnection OpenReadOnly()
    {
        EnsureSafeCatalogFiles();
        var settings=new SqliteConnectionStringBuilder(connectionString) { Mode=SqliteOpenMode.ReadOnly };
        var db=new SqliteConnection(settings.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }
    private static Dictionary<string,string> SchemaObjects(SqliteConnection db,SqliteTransaction? tx)
    {
        using var query=Command(db,tx,"SELECT name,sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT GLOB 'sqlite_*' ORDER BY name LIMIT 101");
        using var reader=query.ExecuteReader(); var objects=new Dictionary<string,string>(StringComparer.Ordinal);
        while(reader.Read()) objects.Add(reader.GetString(0),CatalogMigrations.Normalize(reader.GetString(1)));
        if(objects.Count>100) throw new LabException("catalog_schema_invalid","Catalog contains too many schema objects.");
        return objects;
    }
    private static int SchemaVersion(SqliteConnection db,SqliteTransaction? tx=null)
    {
        var objects=SchemaObjects(db,tx);
        if(!objects.ContainsKey("SchemaMigration"))
        {
            if(objects.Count==0) return 0;
            throw new LabException("catalog_schema_invalid","Nonempty catalog has no migration history.");
        }
        if(objects["SchemaMigration"]!=CatalogMigrations.Objects(1)["SchemaMigration"])
            throw new LabException("catalog_schema_invalid","Migration-history table differs from its declared contract.");
        using var query=Command(db,tx,"SELECT Version FROM SchemaMigration ORDER BY Version LIMIT $maximum",("$maximum",CurrentSchemaVersion+1));
        using var reader=query.ExecuteReader(); int expected=1;
        while(reader.Read())
        {
            long version=reader.GetInt64(0);
            if(version>CurrentSchemaVersion) throw new LabException("schema_unsupported","Catalog was created by a newer schema version.");
            if(version!=expected++) throw new LabException("catalog_schema_invalid","Migration history is not contiguous.");
        }
        if(expected==1) throw new LabException("catalog_schema_invalid","Migration history is empty in a nonempty catalog.");
        return expected-1;
    }
    private static void ValidateSchema(SqliteConnection db,int version,SqliteTransaction? tx=null)
    {
        var actual=SchemaObjects(db,tx); var expected=CatalogMigrations.Objects(version);
        if(actual.Count!=expected.Count||expected.Any(pair=>!actual.TryGetValue(pair.Key,out string? sql)||sql!=pair.Value))
            throw new LabException("catalog_schema_invalid","Catalog tables, indexes, constraints, or additional schema objects do not match the recorded version.");
        ValidateIntegrity(db,tx);
        using var foreign=Command(db,tx,"SELECT * FROM pragma_foreign_key_check LIMIT 1"); using var reader=foreign.ExecuteReader();
        if(reader.Read()) throw new LabException("catalog_schema_invalid","Catalog foreign-key integrity check failed.");
        if(version>=4)
        {
            using var references=Command(db,tx,"""
                SELECT 1 FROM ArtifactReference r LEFT JOIN ArtifactReferenceState s
                ON s.ArtifactId=r.ArtifactId AND s.OwnerId=r.OwnerId
                WHERE s.Revision IS NULL OR s.Released<>0
                OR NOT EXISTS(SELECT 1 FROM Record WHERE Id=r.ArtifactId AND Kind='artifact')
                OR NOT EXISTS(SELECT 1 FROM Record WHERE Id=r.OwnerId)
                UNION ALL
                SELECT 1 FROM ArtifactReferenceState s LEFT JOIN ArtifactReference r
                ON r.ArtifactId=s.ArtifactId AND r.OwnerId=s.OwnerId
                WHERE s.Released=0 AND r.ArtifactId IS NULL LIMIT 1
                """);
            using var states=references.ExecuteReader();
            if(states.Read()) throw new LabException("catalog_schema_invalid","Reference revisions and active references disagree.");
        }
    }
    private static void ValidateIntegrity(SqliteConnection db,SqliteTransaction? tx=null)
    {
        using var query=Command(db,tx,"PRAGMA quick_check(1)");
        if(query.ExecuteScalar() is not string result||result!="ok") throw new LabException("catalog_corrupt","SQLite integrity check failed.");
    }
    private void InitializeSchema(Action<int>? migrationApplied)
    {
        int original=0;
        if(File.Exists(Path.Combine(Root,"catalog.sqlite")))
        {
            using var existing=OpenReadOnly();
            try { original=SchemaVersion(existing); ValidateSchema(existing,original); }
            catch(Exception error) when(error is LabException or SqliteException)
            {
                if(error is SqliteException busy&&busy.SqliteErrorCode is 5 or 6)
                    throw new LabException("catalog_busy","Preflight catalog validation encountered contention; migration was not attempted.");
                var preserved=CreateVerifiedBackup(existing,NewBackupPath("migration-backups"),null);
                throw new LabException(error is LabException lab?lab.Code:"catalog_schema_invalid","Catalog validation failed; migration was not attempted.",
                    "Preserved consistent backup: "+Path.Combine(Root,preserved.RelativePath));
            }
            if(original is >0 and <CurrentSchemaVersion)
                MigrationBackup=CreateVerifiedBackup(existing,NewBackupPath("migration-backups"),original);
        }
        using var db=Open(); using(var mode=Command(db,null,"PRAGMA journal_mode=WAL"))
            if(!string.Equals(mode.ExecuteScalar() as string,"wal",StringComparison.OrdinalIgnoreCase))
                throw new LabException("catalog_unavailable","SQLite could not enable WAL mode.");
        using var tx=db.BeginTransaction();
        if(SchemaVersion(db,tx)!=original) throw new LabException("catalog_schema_changed","Catalog schema changed after preflight validation.");
        ValidateSchema(db,original,tx);
        try
        {
            for(int version=original+1;version<=CurrentSchemaVersion;version++)
            {
                using var migrate=Command(db,tx,CatalogMigrations.Script(version)); migrate.ExecuteNonQuery();
                using var history=Command(db,tx,"INSERT INTO SchemaMigration VALUES($version,$at)",("$version",version),("$at",DateTimeOffset.UtcNow.ToString("O")));
                history.ExecuteNonQuery(); migrationApplied?.Invoke(version);
            }
            ValidateSchema(db,CurrentSchemaVersion,tx);
            if(original!=CurrentSchemaVersion) Audit(db,tx,"catalog_schema_migrated","workspace",Wire.Serialize(new { from=original,to=CurrentSchemaVersion,backup_hash=MigrationBackup?.Hash }));
            tx.Commit();
        }
        catch(Exception error) when(error is not OutOfMemoryException)
        {
            throw new LabException("catalog_migration_failed","Catalog migration rolled back before becoming usable.",
                MigrationBackup is null?"The original empty catalog is preserved.":"Preserved consistent backup: "+Path.Combine(Root,MigrationBackup.RelativePath));
        }
    }
    private string NewBackupPath(string folder)=>Path.Combine(Root,folder,Wire.Id("backup")+".sqlite");
    private string BackupPath(string destination)
    {
        string full=Path.GetFullPath(destination),prefix=Root+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||full.Equals(Path.Combine(Root,"catalog.sqlite"),StringComparison.OrdinalIgnoreCase)||
            !full.EndsWith(".sqlite",StringComparison.OrdinalIgnoreCase))
            throw new LabException("invalid_path","Backups must be new SQLite files beneath this workspace, distinct from the active catalog.");
        EnsureSafePath(full);
        if(File.Exists(full)||File.Exists(full+".manifest.json")) throw new LabException("backup_exists","Backup destinations cannot be overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!); EnsureSafePath(full); return full;
    }
    private CatalogBackup CreateVerifiedBackup(SqliteConnection source,string destination,int? version)
    {
        using var pages=Command(source,null,"PRAGMA page_count"); using var pageSize=Command(source,null,"PRAGMA page_size");
        long estimated=checked(Convert.ToInt64(pages.ExecuteScalar())*Convert.ToInt64(pageSize.ExecuteScalar()));
        if(estimated>2147483648) throw new LabException("resource_limit","Catalog backup exceeds the 2 GiB snapshot budget; source files are preserved.");
        long available=DiskSpace.AvailableBytes(Root);
        if(available<backupDiskReserveBytes||estimated>available-backupDiskReserveBytes)
            throw new LabException("resource_limit","Catalog backup would violate the workspace disk reserve.");
        string full=BackupPath(destination),partial=full+"."+Guid.NewGuid().ToString("N")+".partial";
        using(var target=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=partial,Pooling=false,DefaultTimeout=1 }.ToString()))
        { target.Open(); source.BackupDatabase(target); }
        using(var verify=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=partial,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1 }.ToString()))
        { verify.Open(); ValidateIntegrity(verify); }
        string hash; long length;
        using(var file=new FileStream(partial,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            file.Flush(true); length=file.Length;
            if(length>2147483648) throw new LabException("resource_limit","Catalog grew beyond the snapshot byte budget; unfinished snapshot is preserved.");
            hash=Convert.ToHexStringLower(SHA256.HashData(file));
        }
        var backup=new CatalogBackup(Wire.Id("backup"),Path.GetRelativePath(Root,full),hash,length,version,DateTimeOffset.UtcNow);
        File.Move(partial,full,false);
        using(var manifest=new FileStream(full+".manifest.json",FileMode.CreateNew,FileAccess.Write,FileShare.None))
        { manifest.Write(Encoding.UTF8.GetBytes(Wire.Serialize(backup))); manifest.Flush(true); }
        return backup;
    }
    public CatalogBackup CreateBackup()=>Backup(NewBackupPath("backups"));
    public void VerifyBackup(CatalogBackup backup)
    {
        string full=Path.GetFullPath(Path.Combine(Root,backup.RelativePath));
        if(!full.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new LabException("invalid_path","Backup is outside this workspace.");
        EnsureSafePath(full);
        if(!full.EndsWith(".sqlite",StringComparison.OrdinalIgnoreCase)||backup.Length is <0 or >2147483648)
            throw new LabException("invalid_path","Invalid bounded SQLite backup manifest.");
        if(!File.Exists(full)) throw new LabException("backup_missing","Backup bytes are missing.");
        using var file=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length!=backup.Length||Convert.ToHexStringLower(SHA256.HashData(file))!=backup.Hash)
            throw new LabException("backup_corrupt","Backup hash or length differs from its manifest.");
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=full,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1 }.ToString());
        db.Open(); ValidateIntegrity(db);
        if(backup.SchemaVersion is int version) { if(SchemaVersion(db)!=version) throw new LabException("backup_corrupt","Backup schema version differs."); ValidateSchema(db,version); }
    }
    public CatalogCheckpoint Checkpoint(bool truncate=false)
    {
        if(!writer.Wait(TimeSpan.FromSeconds(5))) throw new LabException("catalog_busy","Checkpoint admission timed out.");
        try
        {
            using var db=Open(); string mode=truncate?"TRUNCATE":"PASSIVE";
            using var command=Command(db,null,truncate?"PRAGMA wal_checkpoint(TRUNCATE)":"PRAGMA wal_checkpoint(PASSIVE)");
            using var reader=command.ExecuteReader(); reader.Read(); return new(reader.GetInt64(0)!=0,reader.GetInt64(1),reader.GetInt64(2),mode);
        }
        finally { writer.Release(); }
    }
}
