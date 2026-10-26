CREATE TABLE RecoverableMutation(TargetId TEXT PRIMARY KEY, OperationId TEXT NOT NULL REFERENCES Operation(Id));
