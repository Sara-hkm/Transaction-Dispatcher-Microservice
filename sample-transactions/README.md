# sample-transactions

Place  transaction files here and they will be picked up by the API when you POST a dispatch request pointing to `/transactions` (the path the API container sees via the Docker volume mount).

Example request (after `docker compose up`):

```http
POST http://localhost:8080/api/dispatch
Content-Type: application/json

{
  "folderPath": "/transactions",
  "deleteAfterSend": false
}
```
