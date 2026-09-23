# Migrating from Fixture.Lib 1.x to 2.0

Behaviour is unchanged. Only API shapes changed. Every 1.x API below was marked `[Obsolete]` in 1.1.0.

## 1. `ValueFormatter.Format` was renamed to `FormatValue`

```csharp
// 1.x
var text = ValueFormatter.Format(amount);
// 2.0
var text = ValueFormatter.FormatValue(amount);
```

## 2. `ConfigClient.GetConfig()` is now `GetConfigAsync(CancellationToken)`

The synchronous method was removed. Make the calling method async and flow a `CancellationToken` from your caller.
Do not block on the task (`.Result`, `.Wait()`, `.GetAwaiter().GetResult()`).

```csharp
// 1.x
public decimal DailyInterest(decimal principal, decimal rate)
{
    var config = _client.GetConfig();
    ...
}

// 2.0
public async Task<decimal> DailyInterestAsync(decimal principal, decimal rate, CancellationToken cancellationToken = default)
{
    var config = await _client.GetConfigAsync(cancellationToken);
    ...
}
```

## 3. `AccountDirectory.GetAccount(string)` now takes an `AccountId`

`AccountId` is a record that validates its value is not empty or whitespace. There is no implicit conversion from `string`.

```csharp
// 1.x
var account = directory.GetAccount("ACC-1");
// 2.0
var account = directory.GetAccount(new AccountId("ACC-1"));
```

`AccountInfo.Id` is still a `string`.
