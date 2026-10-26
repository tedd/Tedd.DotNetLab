CREATE TABLE PolicyRevision(
    Id TEXT PRIMARY KEY, Revision INTEGER NOT NULL UNIQUE CHECK(Revision>0),
    SchemaVersion TEXT NOT NULL, Hash TEXT NOT NULL UNIQUE, Json TEXT NOT NULL, CreatedAt TEXT NOT NULL);
CREATE TABLE Workspace(
    Singleton INTEGER PRIMARY KEY CHECK(Singleton=1), Id TEXT NOT NULL UNIQUE,
    Root TEXT NOT NULL, SchemaVersion INTEGER NOT NULL CHECK(SchemaVersion>0),
    CurrentPolicyId TEXT NOT NULL REFERENCES PolicyRevision(Id), CreatedAt TEXT NOT NULL);
CREATE TABLE OperationPolicy(
    OperationId TEXT PRIMARY KEY REFERENCES Operation(Id), PolicyId TEXT NOT NULL REFERENCES PolicyRevision(Id));
CREATE INDEX OperationPolicy_Policy ON OperationPolicy(PolicyId,OperationId);
