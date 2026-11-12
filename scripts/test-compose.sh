#!/usr/bin/env bash
set -euo pipefail

export JWT_SIGNING_KEY
export JWT_ISSUER=store-platform
export JWT_AUDIENCE=storefront
export SEED_ADMIN_EMAIL=admin@example.test
export SEED_ADMIN_PASSWORD
export RABBITMQ_USER=storeplatform
export RABBITMQ_PASSWORD

JWT_SIGNING_KEY="$(openssl rand -hex 32)"
SEED_ADMIN_PASSWORD="Test-$(openssl rand -hex 16)-Only"
RABBITMQ_PASSWORD="$(openssl rand -hex 24)"

cat > .env <<EOF
JWT_SIGNING_KEY=$JWT_SIGNING_KEY
JWT_ISSUER=$JWT_ISSUER
JWT_AUDIENCE=$JWT_AUDIENCE
SEED_ADMIN_EMAIL=$SEED_ADMIN_EMAIL
SEED_ADMIN_PASSWORD=$SEED_ADMIN_PASSWORD
RABBITMQ_USER=$RABBITMQ_USER
RABBITMQ_PASSWORD=$RABBITMQ_PASSWORD
EOF

temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

echo "Starting containers and waiting for health checks..."
docker compose up --build --detach --wait --wait-timeout 300

gateway="http://localhost:5100"

catalog="$(curl --fail --silent --show-error "$gateway/catalog/v1/products")"
product_id="$(jq -er '.[0].id' <<< "$catalog")"

email="ci-$(openssl rand -hex 8)@example.test"
password="Customer-$(openssl rand -hex 12)"
register_status="$(curl --silent --show-error --output "$temp_dir/register.json" --write-out '%{http_code}' \
  --header 'Content-Type: application/json' \
  --data "$(jq -nc --arg email "$email" --arg password "$password" '{email:$email,password:$password}')" \
  "$gateway/identity/v1/register")"
[[ "$register_status" == "201" ]] || { cat "$temp_dir/register.json"; echo "Registration returned $register_status."; exit 1; }

login_status="$(curl --silent --show-error --output "$temp_dir/login.json" --write-out '%{http_code}' \
  --header 'Content-Type: application/json' \
  --data "$(jq -nc --arg email "$email" --arg password "$password" '{email:$email,password:$password}')" \
  "$gateway/identity/v1/login")"
[[ "$login_status" == "200" ]] || { cat "$temp_dir/login.json"; echo "Login returned $login_status."; exit 1; }
token="$(jq -er '.accessToken' "$temp_dir/login.json")"

submit_order() {
  local simulate_failure="$1"
  curl --fail --silent --show-error \
    --header "Authorization: Bearer $token" \
    --header 'Content-Type: application/json' \
    --data "$(jq -nc --arg product_id "$product_id" --argjson fail "$simulate_failure" \
      '{productId:$product_id,quantity:1,simulatePaymentFailure:$fail}')" \
    "$gateway/orders/v1/orders"
}

wait_for_order() {
  local order_id="$1"
  local order
  local status
  for _ in $(seq 1 60); do
    order="$(curl --fail --silent --show-error \
      --header "Authorization: Bearer $token" \
      "$gateway/orders/v1/orders/$order_id")"
    status="$(jq -r '.status' <<< "$order")"
    case "$status" in
      Confirmed|Cancelled) printf '%s' "$order"; return 0 ;;
    esac
    sleep 1
  done
  echo "Order $order_id remained $status. Last response: $order" >&2
  echo "Orders and RabbitMQ container logs:" >&2
  docker compose logs --no-color orders rabbitmq catalog inventory payments >&2 || true
  return 1
}

successful_response="$(submit_order false)"
successful_id="$(jq -er '.id' <<< "$successful_response")"
successful_status="$(jq -r '.status' <<< "$successful_response")"
[[ "$successful_status" == "Pending" ]] || { echo "Expected async order to start Pending, got $successful_status."; exit 1; }
successful_order="$(wait_for_order "$successful_id")"
[[ "$(jq -r '.status' <<< "$successful_order")" == "Confirmed" ]] || {
  echo "Successful order did not confirm: $successful_order"; exit 1;
}
for expected_status in StockReserved PaymentCharged Confirmed; do
  jq -e --arg status "$expected_status" '.history | any(.status == $status)' \
    <<< "$successful_order" >/dev/null || {
      echo "Order history is missing $expected_status."; exit 1;
    }
done
echo "PASS: RabbitMQ order saga reaches Confirmed"

stock_before="$(curl --fail --silent --show-error "$gateway/inventory/v1/stock/$product_id")"
failed_response="$(submit_order true)"
failed_id="$(jq -er '.id' <<< "$failed_response")"
failed_order="$(wait_for_order "$failed_id")"
[[ "$(jq -r '.status' <<< "$failed_order")" == "Cancelled" ]] || {
  echo "Payment-failure order was not cancelled: $failed_order"; exit 1;
}
stock_after="$(curl --fail --silent --show-error "$gateway/inventory/v1/stock/$product_id")"
[[ "$(jq -r '.onHand' <<< "$stock_before")" == "$(jq -r '.onHand' <<< "$stock_after")" ]] || {
  echo "Compensation changed on-hand stock unexpectedly."; exit 1;
}
[[ "$(jq -r '.reserved' <<< "$stock_after")" == "0" ]] || {
  echo "Compensation left reserved stock behind."; exit 1;
}
echo "PASS: payment failure cancels the order and releases stock"

anonymous_status="$(curl --silent --show-error --output "$temp_dir/anonymous.json" --write-out '%{http_code}' \
  --header 'Content-Type: application/json' \
  --data '{"orderId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","amount":1,"simulateFailure":false}' \
  "$gateway/payments/v1/charges")"
customer_status="$(curl --silent --show-error --output "$temp_dir/customer.json" --write-out '%{http_code}' \
  --header "Authorization: Bearer $token" \
  --header 'Content-Type: application/json' \
  --data '{"orderId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","amount":1,"simulateFailure":false}' \
  "$gateway/payments/v1/charges")"
[[ "$anonymous_status" == "401" && "$customer_status" == "403" ]] || {
  echo "Expected Payments authorization 401/403; got $anonymous_status/$customer_status."; exit 1;
}
echo "PASS: Payments gateway rejects anonymous and non-admin callers (401/403)"
