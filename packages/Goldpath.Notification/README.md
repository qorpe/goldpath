# Goldpath.Notification

One domain decision → one message to one person, with the discipline every project
rewrites badly:

- **Evidence:** every notification is a durable row — "did the customer get the notice?"
  is a QUERY. After body retention nulls the content, the **template hash** still proves
  exactly WHAT was sent. `Sent` means **accepted by the channel** (not delivered/read —
  named honestly).
- **Idempotent:** the REQUIRED unique `dedupKey` makes a retry storm land ONCE.
- **At-most-once-or-repair:** the send run CLAIMS rows (persisted) before any channel
  call; an interrupted send goes to the repair queue for a human-confirmed replay —
  never a silent double-send.
- **Channels are seams:** email (MailKit SMTP) + webhook ship; your SMS gateway plugs in
  as an `IGoldpathNotificationChannel`. Attachments are in the contract from day one.

## Wire it

```csharp
builder.AddGoldpathNotification<WebApplicationBuilder, OrdersDbContext>(notification =>
{
    notification.AddTemplate("policy-renewal", t => t
        .Channel("email", c => c
            .Subject("tr", "Poliçeniz {{PolicyNo}} yenilenmek üzere")
            .Body("tr", "Sayın {{Name}}, poliçeniz {{RenewalDate}} tarihinde yenilenecektir.")
            .Subject("", "Your policy {{PolicyNo}} is up for renewal")
            .Body("", "Dear {{Name}}, your policy renews on {{RenewalDate}}."))
        .DeleteBodyAfter(TimeSpan.FromDays(90)));   // evidence survives, content goes
});

builder.AddGoldpathJobs<WebApplicationBuilder, OrdersDbContext>(jobs =>
{
    jobs.ConnectionName = "ordersdb";
    jobs.AddGoldpathNotificationJobs<OrdersDbContext>();   // send (frequent) + retention (nightly)
});

// DbContext: modelBuilder.AddGoldpathNotification();  modelBuilder.AddGoldpathJobs();
// Config:    Goldpath:Notification:Email { Host, Port, UseSsl, User, Password, From }
```

## Request (the app never sends directly — GP1601)

```csharp
await notifier.RequestAsync(new GoldpathNotificationRequest(
    template: "policy-renewal", channel: "email", recipient: policy.HolderEmail,
    culture: "tr",
    tokens: new Dictionary<string, string> { ["Name"] = holder, ["PolicyNo"] = policy.No, ["RenewalDate"] = date },
    dedupKey: $"renewal:{policy.No}:2026-08")
{
    NotBefore = tomorrowMorning,          // quiet hours are a field, not a policy engine
    Attachments = [new("policy.pdf", "application/pdf", pdfBytes)],
}, ct);
```

Rendering happens AT REQUEST TIME: a missing token throws into YOUR transaction — a bad
notice never persists. The `MaySend` hook records refusals as `Suppressed` rows —
suppression is evidence too.

**A failure says what went wrong, never to whom (#532).** When a channel refuses for the last time, its exception
text becomes the row's `Detail` (the admin surface's notifications, detail and failures views), the jobs console's
repair reason and the warning log line. A provider's words name who it refused: an SMTP reply carries the rejected
address, an SMS gateway's the number. So that text is masked before any of the three sees it, and it fails closed.

- **What is masked:**
  - the row's recipient in any letter case, and its local part on its own;
  - any other address (including `%40`-encoded ones);
  - any run of seven or more digits in any script, with single separators allowed (`+90 555 111 22 33`, `0555-111-22-33`);
  - a URL's query and its credentials. Host and path are kept, and `(credentials removed)` says so.
- **What it can cost:** a date or a long reference number in the provider's text is masked too, because hiding a
  number that was not a phone costs nothing and showing one that was costs a person's data.
- **It is one line and bounded:** control characters become spaces, so the text cannot forge a log line. It is cut
  to 1,024 characters (`Detail`'s column) and ends with `…` when it was longer. A text over 8,192 characters is read
  only up to its last whitespace before that point, so no half address is left to escape the mask. A text that
  cannot be masked in time is withheld entirely.
- **The log line carries the masked text and the exception's type**, not the exception object, because its message
  and its inner exceptions' messages are the provider's unmasked words. The stack trace of a provider's refusal is not
  logged.
- **A row that failed before this rule** is masked on the way out: the admin views mask `Detail` when they read it.
  Its jobs-console repair reason, written when it failed, is not rewritten.

Ops surface (admin API, panel, runbooks) ships in S2; the send run is visible in the
JOBS console today.
