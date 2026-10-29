# ReleaseChecker

Pre-release checks for the oms repo. Runs fully offline. It checks **SQL scripts** (impl_list) and **Config** files (SIT vs UAT vs PROD).

## Run (cmd)

```
dotnet publish -c Release -o C:\tools\ReleaseChecker
C:\tools\ReleaseChecker\ReleaseChecker.exe All --repo C:\src\oms
C:\tools\ReleaseChecker\ReleaseChecker.exe SQL --repo C:\src\oms --country SG --module DRIM
```

First argument = check type: `All` (default), `SQL`, `Config`.

| Option | Meaning |
|---|---|
| `--repo` | Repo root (required) |
| `--country`, `--module` | Limit the SQL check; omit to check everything |
| `--report <file>` | Report path (default `release-check.txt`) |
| `--rules <file>` | Rules file (default `rules.json` next to the exe) |
| `--fail-on-warning` | Exit 1 on warnings too |
| `--impl-list <name>` | SQL: impl list file name (default `impl_list.txt`) |
| `--suppressions <file>` | SQL: one suppression file for all modules |
| `--list-statements` | SQL: list ScriptDom statement class names |
| `--explain <file.sql>` | SQL: class name of each statement in a script |

Exit code `0` = pass, `1` = errors, `2` = bad arguments, layout or config.

## Report

```
================================================================================
ReleaseChecker
================================================================================
Repo      : C:\src\oms
Checks    : All
Result    : FAIL
...
Summary:
  SQL SCRIPTS      FAIL   3 checked, 1 passed, 2 failed
  CONFIG           SKIPPED

================================================================================
SQL SCRIPTS  [FAIL]
================================================================================

SG / DRIM  [FAIL]  3 scripts: 1 passed, 2 failed
--------------------------------------------------------------------------------
1. Table/Orders.sql - FAIL
     - Line 1: Listed in impl_list but file not found. [IMPL_MISSING_FILE]
2. Config/ConfigParam.sql - FAIL
     - Line 6: DROP TABLE dbo.ConfigParamBackup is not allowed. ... [DROP_TABLE_NOT_ALLOWED | DropTableStatement]
3. GrantAccess.sql - PASS
```

A script FAILS if it has at least one open error. Warnings (`Warning:`) and accepted findings (`Accepted:`) are listed but don't fail it.

## Project layout (adding a check)

```
Program.cs                 parses args, runs checks from the registry, writes console + report
Core/
  CliOptions.cs            arguments (All | SQL | Config + options)
  IReleaseCheck.cs         IReleaseCheck + CheckSection (what every check returns)
  CheckRegistry.cs         list of checks - register new ones here
  ReleaseRules.cs          rules.json root: one section per check
  ReportWriter.cs          report header, summary, one section per check
  Finding.cs, ConsoleOut.cs
Checks/Sql/                SQL SCRIPTS check
Checks/Config/             CONFIG check
```

To add another check:
1. Implement `IReleaseCheck` in `Checks/<Name>/` and return a `CheckSection` subclass (see `SqlSection`) that writes its own report and console body.
2. Add its settings class to `ReleaseRules` and a matching section in `rules.json`.
3. Register it in `CheckRegistry`; add a value to `CheckMode` if it needs its own argument.

## SQL check

Layout:
```
<repo>\<Country>\Database\scripts\<Module>\impl_list.txt
<repo>\<Country>\Database\scripts\<Module>\sqlcheck.suppressions.json   (optional)
<repo>\<Country>\Database\sql\<Module>\...\*.sql
```

Only the scripts listed in impl_list are checked (one file lookup each, no folder scanning).

Rules live in the `"sql"` section of `rules.json`: allowed statements, denied CREATE/ALTER kinds, droppable table suffixes,
truncate on staging, parser version, and `severityOverrides` (`Error`, `Warning`, `Info`, `Off`; `SYNTAX_ERROR` can't change).

### Grant check

When the module creates a table or stored procedure, the grant script in impl_list (`GrantAccess.sql` by default)
must grant the configured permissions. Configured in `"sql" > "grantCheck"` in rules.json:

```json
"grantCheck": {
  "enabled": true,
  "grantScripts": [ "GrantAccess.sql" ],
  "requireGrantScript": true,
  "required": {
    "Table":     [ { "principal": "SEL", "permissions": [ "SELECT" ] },
                   { "principal": "RIM", "permissions": [ "SELECT", "INSERT", "UPDATE", "DELETE" ] } ],
    "Procedure": [ { "principal": "RIM", "permissions": [ "EXECUTE" ] } ]
  },
  "excludeObjects": [ "*Stg" ]
}
```

- Object types: `Table`, `Procedure`, `View`, `Function` (CREATE and CREATE OR ALTER; ALTER alone isn't "new").
- A missing grant is a `GRANT_MISSING` error on the grant script, with the exact GRANT statement to add.
- `GRANT ... ON SCHEMA::dbo TO [RIM]` and `GRANT ALL` count as covering the object.
- No grant script listed but objects created -> `GRANT_SCRIPT_NOT_LISTED` warning (`requireGrantScript: false` to turn off).
- Temp tables are ignored; use `excludeObjects` for anything else that doesn't need grants.

### Accepting a finding by design (sqlcheck.suppressions.json)

```json
{
  "suppressions": [
    {
      "rule": "UPDATE_NO_WHERE",
      "file": "Config/ConfigParam.sql",
      "contains": "UPDATE dbo.ConfigParam SET ParamValue",
      "reason": "Release resets every config flag to Y by design",
      "approvedBy": "Marc",
      "expires": "2027-06-30"
    }
  ]
}
```
`reason` is required. Errors can't be suppressed unless `allowSuppressingErrors` is true; syntax errors never.

### Rule IDs

| Rule ID | Default |
|---|---|
| `SYNTAX_ERROR` | Error (fixed) |
| `STATEMENT_NOT_ALLOWED`, `DROP_TABLE_NOT_ALLOWED`, `TRUNCATE_NOT_ALLOWED` | Error |
| `IMPL_MISSING_FILE`, `IMPL_DUPLICATE`, `IMPL_SQL_FOLDER_MISSING`, `SUPPRESSION_FILE_INVALID` | Error |
| `GRANT_MISSING` | Error |
| `GRANT_SCRIPT_NOT_LISTED` | Warning |
| `UPDATE_NO_WHERE`, `DELETE_NO_WHERE`, `STATEMENT_UNVERIFIABLE` (EXEC), `DROP_TABLE_NO_IF_EXISTS` | Warning |
| `CREATE_TABLE_NOT_GUARDED`, `CREATE_INDEX_NOT_GUARDED`, `ALTER_TABLE_ADD_NOT_GUARDED`, `ALTER_TABLE_DROP_NOT_GUARDED` | Warning |
| `TRAN_NO_COMMIT`, `TRAN_COUNT_MISMATCH` | Warning |
| `ORDER_USED_BEFORE_CREATE`, `ORDER_USED_BEFORE_CREATE_IN_MODULE`, `ORDER_CREATED_TWICE` | Warning |
| `IMPL_NOT_SQL`, `IMPL_EMPTY`, `SCRIPT_EMPTY` | Warning |
| `SUPPRESSION_UNUSED`, `SUPPRESSION_EXPIRED`, `SUPPRESSION_NO_REASON`, `SUPPRESSION_NOT_PERMITTED` | Warning |

## Config check

Layout: `--config-path` argument, else `"config" > "pathTemplate"` in rules.json, default `{country}/App/Config/{env}/Web/{service}`.
If the folders move, just pass a new pattern (relative to `--repo`, must end with `{service}`):
```
ReleaseChecker.exe Config --repo C:\src\oms --config-path "{country}/App/Config/{env}/Web/{service}"
```
```
oms\SG\App\Config\SIT\Web\RIMCoreOrderService\appsettings.json, nlog.config, web.config
oms\SG\App\Config\UAT\Web\RIMCoreOrderService\...
oms\SG\App\Config\PROD\Web\RIMCoreOrderService\...
```

Per country and service:
1. The service folder and every file in `files` exist in every environment.
2. **Key parity**: the same keys exist in SIT, UAT and PROD. appsettings keys use .NET notation (`Services:AccountApi:BaseUrl`);
   XML keys use element paths with identity attributes (`configuration:appSettings:add[BaseUrl]:@value`), so order doesn't matter.
3. **Must differ**: database and base URL values are different in every environment. Connection strings are compared on
   server + database (so a different timeout doesn't count as "different"); URLs on host + port.
4. **Service groups**: e.g. `RIMCoreOrderService` and `RIMCoreAppInterface` must have the same `appsettings.json` (same keys
   and values) but a different `nlog.config`, in each environment. Groups are skipped in a country that doesn't have both services.

Values are never written to the report except server/database names and URL hosts (connection strings may hold passwords).
Use `ignoreKeys` for keys that are environment-specific by design.

```
SG  [FAIL]  2 services: 0 passed, 2 failed
--------------------------------------------------------------------------------
1. RIMCoreAppInterface - FAIL
     - [appsettings.json] 'Services:AccountApi:BaseUrl': UAT, PROD point to the same host (prod-api.bank.local:443) - it must be different per environment. [CONFIG_SAME_ACROSS_ENV]
     - [appsettings.json] PROD: must be the same as RIMCoreOrderService but differs - extra: Services:AccountApi:Timeout [CONFIG_GROUP_NOT_SAME]
     - [nlog.config] SIT: identical to RIMCoreOrderService - it must be specific to RIMCoreAppInterface. [CONFIG_GROUP_NOT_DIFFERENT]
2. RIMCoreOrderService - FAIL
     - [appsettings.json] 'Services:AccountApi:Timeout' missing in PROD (present in SIT, UAT). [CONFIG_KEY_MISSING]
     ...
```

### Config rule IDs (change with `"config" > "severityOverrides"`)

| Rule ID | Default | Meaning |
|---|---|---|
| `CONFIG_ENV_MISSING` | Warning | Environment folder not found for a country (left out of the comparison) |
| `CONFIG_SERVICE_MISSING` | Warning | Service folder in some environments but not others (compared where it exists) |
| `CONFIG_FILE_MISSING` | Error | appsettings.json / nlog.config / web.config missing |
| `CONFIG_INVALID_FILE` | Error | File can't be parsed |
| `CONFIG_DUPLICATE_KEY` | Error | Key declared twice in one file (last one silently wins) |
| `CONFIG_KEY_MISSING` | Error | Key present in some environments but not others |
| `CONFIG_SAME_ACROSS_ENV` | Error | Database / base URL is the same in two or more environments |
| `CONFIG_GROUP_NOT_SAME` | Error | File that must match the group's first service differs |
| `CONFIG_GROUP_NOT_DIFFERENT` | Error | File that must be service-specific is identical |
