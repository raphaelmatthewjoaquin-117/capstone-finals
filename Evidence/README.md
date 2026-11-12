# Demo evidence to capture

Save genuine screenshots or a short screencast in this folder. Do not use placeholders or screenshots of mocked results.

The Compose stack is the primary demo environment because it enables RabbitMQ. Start with a clean stack and a local `.env`, then open the Storefront at `http://localhost:5200`. Do not show or include `.env` or credentials in captured media.

Capture these acceptance points, ideally in one short recording and supporting screenshots:

1. **Successful order:** browse, register or log in, place an order, and show its Confirmed status/history.
2. **Compensation:** enable the Storefront's simulated payment-failure option, place the order, show Cancelled status/history, and show that the reserved stock was released.
3. **Retry:** on a development-mode run, call `POST /catalog/v1/chaos/fail-next/1`, then place an order and capture the transient Catalog failure followed by a successful retry. The chaos route is intentionally unavailable in the Production Compose configuration.
4. **Correlation ID:** send or capture an `X-Correlation-ID` on a gateway request and show the same ID in gateway/service logs. For an asynchronous order, also show it in the Orders consumer log/message trace.
5. **RabbitMQ and DLQ:** open `http://localhost:15672` and show the event queues and at least one queue ending in `.dlq`. The queues are created by the application when its consumers/publisher start.
6. **Gateway security:** show an anonymous admin request returning 401 and an authenticated Customer attempting the same Admin-only route and receiving 403.
7. **Clean start:** begin the recording by starting the stack with `docker compose up --build`, then browse through the gateway-backed Storefront. Include the blocked Admin action before ending.

The Windows fallback runner disables RabbitMQ and cannot provide the broker/async evidence. If Docker is unavailable on the recording device, use a Docker-capable environment; do not present local synchronous mode as proof of the RabbitMQ flow.

Suggested filenames: `demo-walkthrough.mp4`, `successful-order.png`, `compensation.png`, `retry-correlation.png`, `rabbitmq-queues.png`, and `gateway-401-403.png`.
