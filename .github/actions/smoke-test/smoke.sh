#!/usr/bin/env bash
# Post-deployment smoke test. Two modes:
#   smoke.sh app  <application-url>   the application directly (App Service slot, Container App): from a runner in the VNet
#   smoke.sh edge <public-url>        the whole path internet → Front Door → APIM → application
#
# WHY: reads and refusals only. The smoke test also runs in production, so it places no orders
# (a test order would start the saga, a stock reservation and a payment). The full order scenario in dev and test
# is an availability test in Application Insights with a test account, outside the pipeline.
set -euo pipefail

mode="$1"
base_url="${2%/}"
random_order_id="$(cat /proc/sys/kernel/random/uuid)"

# expect <opis> <oczekiwany-status> <url>
expect() {
  local description="$1" expected="$2" url="$3" status
  status=$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 15 "$url" || echo "000")
  if [ "$status" = "$expected" ]; then
    echo "OK    $description ($status)"
  else
    echo "::error title=Smoke test::$description: expected $expected, got $status ($url)"
    return 1
  fi
}

# After a deployment the application is starting (loading configuration from App Configuration, warming up clients), so we
# first wait for readiness instead of judging the first response.
wait_ready() {
  local url="$1"
  for attempt in $(seq 1 30); do
    if [ "$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 10 "$url" || true)" = "200" ]; then
      echo "OK    ready after $attempt attempts"
      return 0
    fi
    sleep 10
  done
  echo "::error title=Smoke test::$url did not answer 200 within 5 minutes"
  return 1
}

case "$mode" in
  app)
    wait_ready "$base_url/health/ready"
    expect "liveness" 200 "$base_url/health/live"
    # 401, not 404 or 500: routing works, authentication is enabled and rejects a request without a token.
    expect "order without a token" 401 "$base_url/v1/orders/$random_order_id"
    # Dependency diagnostics require a token (they contain dependency names), so here we only check they are not public.
    expect "dependency diagnostics without a token" 401 "$base_url/health/dependencies"
    ;;
  edge)
    # The catalog is public and cached in Front Door: the full path to inventory-api and the SQL database.
    wait_ready "$base_url/catalog/products?pageSize=1"
    expect "order without a token (validate-jwt in APIM)" 401 "$base_url/shop/v1/orders/$random_order_id"
    # APIM does not expose /health/*: diagnostic endpoints are not reachable from the internet.
    expect "health niewystawiony" 404 "$base_url/shop/health/ready"
    ;;
  *)
    echo "Unknown mode: $mode (app|edge)" >&2
    exit 2
    ;;
esac
