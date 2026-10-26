CREATE TABLE ArtifactReference(ArtifactId TEXT NOT NULL, OwnerId TEXT NOT NULL, RetainUntil TEXT, PRIMARY KEY(ArtifactId,OwnerId));
CREATE TABLE ArtifactDeletion(ArtifactId TEXT PRIMARY KEY, PlanId TEXT NOT NULL, State TEXT NOT NULL, CreatedAt TEXT NOT NULL, CompletedAt TEXT);
