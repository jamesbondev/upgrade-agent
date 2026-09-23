# Changelog

## 2.0.0

Breaking release. See MIGRATION.md.

- Removed `ValueFormatter.Format(decimal)`. Use `ValueFormatter.FormatValue(decimal)`.
- Removed `ConfigClient.GetConfig()`. Use `ConfigClient.GetConfigAsync(CancellationToken)`.
- Removed `AccountDirectory.GetAccount(string)`. Use `AccountDirectory.GetAccount(AccountId)`.

## 1.1.0

- Added `ValueFormatter.FormatValue(decimal)`, `ConfigClient.GetConfigAsync(CancellationToken)`, `AccountId` and `AccountDirectory.GetAccount(AccountId)`.
- Marked `Format`, `GetConfig` and `GetAccount(string)` as `[Obsolete]`. They will be removed in 2.0.

## 1.0.0

- Initial release.
