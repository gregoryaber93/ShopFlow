# Runbook: secret rotation

A planned procedure (every 90 days, or immediately when a leak is suspected). ShopFlow has only third-party secrets;
everything else authenticates with a managed identity and there is nothing to rotate ([ADR-0010](../adr/0010-managed-identities-and-app-roles.md)).

## Inventory

| Secret (Key Vault `kv-shopflow-{env}-weu`) | Used by | How it reaches the application | Refresh without a restart |
|---|---|---|---|
| `psp-api-key` | `payments-processor` → PSP | App Configuration `Payments:Psp:ApiKey` (a reference without a version) | up to 1 h (`SetSecretRefreshInterval`) or ~30 s after changing `Shared:Sentinel` |
| `psp-webhook-secret-current`, `psp-webhook-secret-previous` | `payments-processor` (webhook HMAC signature) | `Payments:Psp:WebhookSecrets:0` and `:1` | as above |
| `carrier-api-key` | `shipping-processor` → carrier | `Shipping:Carrier:ApiKey` | as above |
| `carrier-webhook-token` | APIM (the `carrier-webhook.xml` policy) | a named value linked to Key Vault | APIM refreshes every few hours or on demand (`refreshSecret`) |

What we do **not** rotate, because it does not exist: Service Bus, Cosmos, Storage, Redis and App Configuration keys, SQL
passwords, the Application Insights instrumentation key (local auth disabled), pipeline secrets (OIDC), a client secret
for Microsoft Graph (federated credential).

## Preparation

- The **Key Vault Secrets Officer** role on the vault and **App Configuration Data Owner** activated in PIM.
- Key Vault and App Configuration have only private endpoints: data-plane commands from a machine in the VNet.
- Never type the new value on the command line (shell history, logs). Use an in-memory temporary file with `--file` and
  delete it afterwards.

```bash
KV=kv-shopflow-$ENV-weu
APPCS=appcs-shopflow-$ENV-weu
bump_sentinel() {
  az appconfig kv set --name $APPCS --auth-mode login --key Shared:Sentinel --value "$(date -u +%Y%m%dT%H%M%SZ)" --yes
}
```

## PSP API key (`psp-api-key`)

Assumes the PSP allows two active keys at once (standard for payment providers).

1. Generate a new key in the PSP panel. **The old one stays active.**
2. Save a new secret version: `az keyvault secret set --vault-name $KV --name psp-api-key --file /dev/shm/psp-key --encoding utf-8`.
3. `bump_sentinel`: the applications reload their configuration within ~30 s and resolve the reference to the latest version.
4. Check that PSP calls succeed: [payments.kql](../kql/payments.kql), section "PSP calls"; no 401s.
   In the PSP panel the new key has traffic and the old one stops having it.
5. After at least 1 h (the maximum refresh interval) disable the old key in the PSP panel.
6. Disable the previous secret version: `az keyvault secret set-attributes --vault-name $KV --name psp-api-key --version <old> --enabled false`.

Rollback: if 401s appear in step 4, restore the previous version as the latest one (`az keyvault secret set` with the old
value) and `bump_sentinel`.

## PSP webhook signing secret (two slots)

The verifier accepts a signature that matches either of the two secrets, so a rotation loses no webhooks.

1. Save the current value of `psp-webhook-secret-current` as a new version of `psp-webhook-secret-previous`.
2. Generate a new secret (in "pending" mode in the PSP panel, if the PSP supports it) and save it as a new version of
   `psp-webhook-secret-current`.
3. `bump_sentinel` and wait a minute: the application now accepts both the old and the new signature.
4. Switch signing to the new secret in the PSP panel.
5. Check: webhooks get through (`psp-webhook` without 401s in [payments.kql](../kql/payments.kql)).
6. After a day (the PSP's webhook retry window) set `psp-webhook-secret-previous` to the same value as `current`.

**Do not delete or disable** `psp-webhook-secret-previous`: the reference in App Configuration would stop resolving and
`payments-processor` would not start on the next restart.

## Carrier API key (`carrier-api-key`)

Like the PSP key: a new key at the carrier, a new secret version, `bump_sentinel`, verifying the calls from
`shipping-processor`, disabling the old key after an hour.

## Carrier webhook token (`carrier-webhook-token`, APIM)

The APIM policy compares the `Authorization` header with a single value, so between the change in APIM and the change
at the carrier webhooks get 401. The carrier retries webhooks, but the switch must be coordinated.

1. Agree the switch time with the carrier.
2. Save a new version of `carrier-webhook-token` in Key Vault.
3. At the agreed time force a refresh of the named value in APIM:

```bash
APIM_ID=$(az apim show -g $RG_APPS -n apim-shopflow-$ENV-weu --query id -o tsv)
az rest --method post --url "https://management.azure.com$APIM_ID/namedValues/carrier-webhook-token/refreshSecret?api-version=2024-05-01"
```

4. The carrier switches the token. Check for 401s on `/webhooks/carrier` in the gateway logs (`ApiManagementGatewayLogs`).

A reported gap: a second named value (`carrier-webhook-token-next`) and a second value in `check-header` would allow a
rotation without a 401 window, as for the PSP webhooks.

## When a leak is suspected

The order is the reverse of a planned rotation: first **revoke the old secret at the provider**, then upload the new one.
A few minutes of 401s (messages wait in the queues) is better than an active leak. Check in the Key Vault logs
(`AzureDiagnostics` / `KeyVaultLogs`) who read the secret in recent days.
