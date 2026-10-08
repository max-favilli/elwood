SET QUOTED_IDENTIFIER ON;
IF OBJECT_ID('dbo.imports') IS NULL CREATE TABLE dbo.imports(blob_path nvarchar(450) NOT NULL, etag nvarchar(100) NOT NULL, payload nvarchar(max) NOT NULL, loaded_at datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), CONSTRAINT pk_imports PRIMARY KEY(blob_path,etag));
GO
CREATE OR ALTER PROCEDURE dbo.GetProduct @id int AS
SELECT @id AS id, N'SKU-42' AS sku, 7 AS quantity;
GO
CREATE OR ALTER PROCEDURE dbo.ImportOffers @payload nvarchar(max) AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 DECLARE @path nvarchar(450)=JSON_VALUE(@payload,'$.import.blobPath'), @etag nvarchar(100)=JSON_VALUE(@payload,'$.import.etag');
 BEGIN TRANSACTION;
 IF EXISTS(SELECT 1 FROM dbo.imports WITH(UPDLOCK,HOLDLOCK) WHERE blob_path=@path AND etag=@etag)
  BEGIN COMMIT; SELECT 'duplicate' AS status, 0 AS offers_loaded; RETURN; END;
 INSERT dbo.imports(blob_path,etag,payload) VALUES(@path,@etag,@payload);
 COMMIT;
 SELECT 'loaded' AS status, (SELECT COUNT(*) FROM OPENJSON(@payload,'$.offers')) AS offers_loaded;
END;
GO
-- Runtime SID is supplied separately by the operator, without a directory lookup.
