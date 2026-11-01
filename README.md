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

Database root: `--db-path` argument, else `"sql" > "dbPath"`, default `{country}/Database` (relative to `--repo` or
absolute; `{country}` is optional). For the published collection without country folders:
```
ReleaseChecker.exe SQL --repo D:\Release\R2026.11 --db-path "Database"
```

Scripts must be **UTF-8** (`ENCODING_NOT_UTF8`, error). ANSI / Windows-1252 and UTF-16 files are caught with the line of
the first bad byte. Optional BOM policy: `"sql" > "fileEncoding" > "utf8Bom"` = `Any` | `Required` | `Forbidden` (`ENCODING_BOM`).

Layout (default):
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
| `ENCODING_NOT_UTF8`, `ENCODING_BOM` | Error |
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

Environment root: `--config-path` argument, else `"config" > "pathTemplate"`, default `{country}/App/Config/{env}`
(relative to `--repo` or absolute; `{env}` required, `{country}` optional). If the folders move, just pass a new pattern:
```
ReleaseChecker.exe Config --repo C:\src\oms
ReleaseChecker.exe Config --repo C:\src\web-repo --config-path "{country}/Web/Config/{env}"
```

Every folder below the environment root, at any depth, that holds `appsettings.json`, `nlog.config` or `web.config`
is an **application**, so web services and batch programs are found without listing them:
```
SG\App\Config\SIT\Online\Web\V2\RIMCoreOrderService\         -> Online/Web/V2/RIMCoreOrderService
SG\App\Config\SIT\Batch\Programs\RIMSInternal\RIMS.EDAG.V2\   -> Batch/Programs/RIMSInternal/RIMS.EDAG.V2
```

Per country and application:
1. **Folders and files tally**: an application missing in an environment is a warning; a file present in one
   environment but missing in another is an error. `requiredIn` makes a file mandatory (web.config only for `*Web*`).
2. **Key parity**: the same keys exist in SIT, UAT and PROD (all three files).
3. **Required keys**: e.g. the full `WMQConfiguration` block (Active/Passive MQ manager, Enquiry/Transaction/Composite
   queues, UserId, ChannelId) must be present and non-empty in every environment, for applications that use MQ.
4. **Must differ**: database (server + database), base URL host and MQ host are different in every environment.
5. **Groups**: e.g. `RIMCoreOrderService` and `RIMCoreAppInterface` have the same `appsettings.json` and a
   different `nlog.config` per environment (matched by folder name).

Values are never written to the report except server/database names and host names.

```
SG  [FAIL]  5 applications: 1 passed, 4 failed
--------------------------------------------------------------------------------
1. Batch/Programs/RIMSInternal/RIMS.EDAG.V2 - PASS
2. Batch/Programs/RIMSInternal/RIMS.Programs.V2 - FAIL
     - [appsettings.json] PROD: WMQConfiguration incomplete - missing WMQConfiguration:PassiveMQManager:Channel [CONFIG_REQUIRED_KEY_MISSING]
     - [appsettings.json] PROD: WMQConfiguration has empty values - WMQConfiguration:UserId [CONFIG_REQUIRED_KEY_EMPTY]
     - [appsettings.json] 'WMQConfiguration:ActiveMQManager:Hostname': UAT, PROD point to the same host (mq-prod01) ... [CONFIG_SAME_ACROSS_ENV]
3. Online/Web/V2/RIMCoreAppInterface - FAIL
     ...
5. Online/Web/V2/RIMCoreSharedService - PASS
     Warning: Application folder missing in UAT, PROD (present in SIT). [CONFIG_APP_MISSING]
```

### Config rule IDs (change with `"config" > "severityOverrides"`)

| Rule ID | Default | Meaning |
|---|---|---|
| `CONFIG_ENV_MISSING` | Warning | Environment folder not found (left out of the comparison) |
| `CONFIG_APP_MISSING` | Warning | Application folder in some environments but not others (compared where it exists) |
| `CONFIG_FILE_MISSING` | Error | Required file missing, or file present in one environment but not another |
| `CONFIG_INVALID_FILE` | Error | File can't be parsed |
| `CONFIG_DUPLICATE_KEY` | Error | Key declared twice in one file (last one silently wins) |
| `CONFIG_KEY_MISSING` | Error | Key present in some environments but not others |
| `CONFIG_REQUIRED_KEY_MISSING` | Error | Required key (e.g. WMQConfiguration) missing |
| `CONFIG_REQUIRED_KEY_EMPTY` | Error | Required key has an empty value |
| `CONFIG_SAME_ACROSS_ENV` | Error | Database / base URL / MQ host is the same in two or more environments |
| `CONFIG_GROUP_NOT_SAME` | Error | File that must match the group's first application differs |
| `CONFIG_GROUP_NOT_DIFFERENT` | Error | File that must be application-specific is identical |

