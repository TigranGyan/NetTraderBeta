-- Run this on your PostgreSQL database if the migration was never applied
-- (e.g. column "StopLoss" does not exist). Use the same DB as your app.

-- 1) Add the new columns
ALTER TABLE "TradeSessions" ADD COLUMN IF NOT EXISTS "StopLoss" numeric NOT NULL DEFAULT 0;
ALTER TABLE "TradeSessions" ADD COLUMN IF NOT EXISTS "TakeProfit" numeric NOT NULL DEFAULT 0;

-- 2) Tell EF Core this migration is applied (so it won't run again)
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260312000000_AddStopLossTakeProfitToTradeSession', '9.0.2')
ON CONFLICT ("MigrationId") DO NOTHING;
