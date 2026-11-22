#!/usr/bin/env bash
# One-time Entra ID bootstrap for an environment (run by an administrator, not by the pipeline).
# Creates the app registrations and groups whose identifiers go into infra/parameters/<env>.bicepparam.
#
# WHY: app registrations are tenant objects, not subscription objects. They require Entra ID roles (Application
# Administrator) that the pipeline deploying Azure resources deliberately does not have. Bicep gets the ready identifiers as data.
#
# Usage: ./bootstrap-entra-apps.sh dev
set -euo pipefail

ENV="${1:?Provide the environment: dev | test | prod}"
SUFFIX=$([ "$ENV" = "prod" ] && echo "" || echo "-$ENV")

create_api() {
  local name="$1" uri="$2"; shift 2
  local app_id
  app_id=$(az ad app create --display-name "$name" --identifier-uris "$uri" --sign-in-audience AzureADMyOrg --query appId -o tsv)
  # App roles granted to managed identities (client credentials, no scp in the token).
  # One call with the whole list: --app-roles replaces the role array rather than appending to it.
  if [ "$#" -gt 0 ]; then
    local roles="" role
    for role in "$@"; do
      roles+="{\"allowedMemberTypes\":[\"Application\"],\"displayName\":\"$role\",\"description\":\"$role\",\"isEnabled\":true,\"value\":\"$role\",\"id\":\"$(uuidgen)\"},"
    done
    az ad app update --id "$app_id" --app-roles "[${roles%,}]"
  fi
  az ad sp create --id "$app_id" >/dev/null
  echo "$name: appId=$app_id uri=$uri"
}

echo "== APIs (token audiences, app roles)"
create_api "shopflow-orders$SUFFIX" "api://shopflow-orders$SUFFIX" "Orders.Internal.Write"
create_api "shopflow-inventory$SUFFIX" "api://shopflow-inventory$SUFFIX" "Catalog.Prices.Read" "Stock.Receipts.Write"
create_api "shopflow-readmodels$SUFFIX" "api://shopflow-readmodels$SUFFIX"

echo "== Easy Auth of HTTP functions (roles checked in code by EasyAuthPrincipal)"
create_api "shopflow-func-order-saga$SUFFIX" "api://shopflow-func-order-saga$SUFFIX" "Saga.Operator"
create_api "shopflow-func-payments$SUFFIX" "api://shopflow-func-payments$SUFFIX" "Payments.Webhooks.Write"
create_api "shopflow-func-shipping$SUFFIX" "api://shopflow-func-shipping$SUFFIX" "Carrier.Webhooks.Write" "Warehouse.Operator"
create_api "shopflow-func-notifications$SUFFIX" "api://shopflow-func-notifications$SUFFIX" "Support.Reader"
create_api "shopflow-func-analytics$SUFFIX" "api://shopflow-func-analytics$SUFFIX" "Analytics.Ingest" "Analytics.Reader"

echo "== SQL administrators group (membership through Privileged Identity Management)"
az ad group create --display-name "sg-shopflow-sql-admins-$ENV" --mail-nickname "sg-shopflow-sql-admins-$ENV" --query id -o tsv

cat <<'NEXT'
Next steps (manually or with a separate script):
  1. External ID tenant: registration "shopflow-customer-directory" with the Microsoft Graph application permission
     User.Read.All (admin consent) and a federated credential:
       issuer  = https://login.microsoftonline.com/<company tenant>/v2.0
       subject = <objectId of the id-shopflow-notifications-processor-<env>-weu identity>
       audience = api://AzureADTokenExchange
  2. After the first Bicep deployment: assign app roles to the managed identities
     (e.g. order-saga → Orders.Internal.Write, orders-api → Catalog.Prices.Read, APIM → *.Webhooks.Write, Analytics.Ingest).
  3. Fill in the identifiers in infra/parameters/<env>.bicepparam.
NEXT
