# The WhatsApp agent

Design and decisions: `docs/MCP-DESIGN.md`. This file is how to switch it on and what to check.

## What it is

```
WhatsApp -> /webhooks/whatsapp -> inbox -> AgentProcessor -> AgentRunner -> Claude
                                                  |               |
                                      buttons / yes-no / undo     MCP tools (in memory) -> the existing REST API
```

- Code: `JewelryManager.Api/Features/Agent/` (channel, runner, memory, safety) and `JewelryManager.Mcp/` (the tools).
- The agent calls the REST API with a shared key and the id of the whitelisted person; the API resolves the tenant exactly as for a signed-in user.
- It stays **completely off** until `Agent__Enabled=true`.

## Settings (environment variables, never in git)

| Variable | What |
|---|---|
| `Agent__Enabled` | `true` to switch on. The server refuses to start if a required secret below is missing |
| `Agent__ServiceKey` | A long random secret (32+ characters) shared between the agent and the API |
| `Agent__WhatsApp__PhoneNumberId` | From Meta (WhatsApp > API setup) |
| `Agent__WhatsApp__AccessToken` | Permanent system-user token |
| `Agent__WhatsApp__AppSecret` | Used to verify that a webhook really came from Meta |
| `Agent__WhatsApp__VerifyToken` | Any random string you also type into Meta's webhook setup |
| `Agent__Model__ApiKey` or `ANTHROPIC_API_KEY` | Claude API key |
| `Agent__Model__Name` | Default `claude-haiku-5-5` (the cheapest; move up only where it fails the evaluation) |
| `Agent__Transcription__ApiKey` | Key of a service with the OpenAI-style `/audio/transcriptions` endpoint. Empty = voice notes answer "not connected" |
| `Agent__Transcription__BaseUrl` / `__Model` | Defaults: OpenAI, `whisper-1`. Choose by testing Hebrew voice notes |
| `Agent__Limits__DailyTokensPerUser` / `__MonthlyTokensPerBusiness` | Budgets (defaults 200,000 and 3,000,000 tokens) |
| `Agent__Proactive__*Template` | Names of the approved WhatsApp templates (each has one text variable `{{1}}`) |

## Switching it on, in order

1. Apply the migration (`dotnet ef database update`, or start the server: it migrates itself).
2. Meta: create the app, add a WhatsApp number for the bot, and set the webhook to `https://<server>/webhooks/whatsapp` with the verify token. Subscribe to `messages`.
3. Set the environment variables above and restart.
4. Add the people who may talk to the bot (signed in as an owner, e.g. with the app's token):
   - `POST /agent/users` with `{ "phone": "9725XXXXXXXX", "email": "<their account email>", "gender": "female", "isDeveloper": false }`
   - `GET /agent/users` lists them; `DELETE /agent/users/{id}` switches a number off at once.
   - A number that is not on the list gets **no answer at all**.
5. Send "היי" from a listed number. Test on a test business with invented data first.

## What protects the business

- The webhook checks Meta's signature on every call; messages are processed once (by id).
- Only listed numbers are answered; the business comes from the list, never from the message.
- The model can only use tools listed in `ToolPolicy`; the API only accepts the calls listed in `AgentRouteGuard` from the agent's identity (no deletes except undoing what the agent itself just created, no settings, no users).
- Anything that changes the books waits for an approve button, which the server runs without asking the model again. Three small changes (next stage, receipt sent, customer notified) run at once and can be undone with "בטלי" within 10 minutes.
- Customer phone numbers, salary, hourly rate and work hours are never given to the model.
- Token limits: at most 6 tool calls per message, short answers, short memory, daily and monthly budgets, messages-per-minute limit.

## Known limits (be aware)

- Messages wait in memory between the webhook and the worker: if the server restarts at that moment, a message can be lost. The person simply sends it again.
- The invoice photo is held in memory while its proposal waits for approval (10 minutes); after a restart the expense is saved without the file.
- Voice and Hebrew quality depend on the transcription service you choose: test with real recordings before relying on it.
- The WhatsApp 24-hour rule: the agent's own messages (morning summary...) outside the window need an approved template.
