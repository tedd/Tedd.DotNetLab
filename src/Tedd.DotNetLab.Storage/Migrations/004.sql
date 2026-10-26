CREATE TABLE ArtifactReferenceState(
    ArtifactId TEXT NOT NULL, OwnerId TEXT NOT NULL,
    Revision INTEGER NOT NULL CHECK(Revision>0), Released INTEGER NOT NULL CHECK(Released IN (0,1)),
    PRIMARY KEY(ArtifactId,OwnerId));
INSERT INTO ArtifactReferenceState SELECT ArtifactId,OwnerId,1,0 FROM ArtifactReference;
