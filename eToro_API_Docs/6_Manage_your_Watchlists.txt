> ## Documentation Index
> Fetch the complete documentation index at: https://api-portal.etoro.com/llms.txt
> Use this file to discover all available pages before exploring further.

# Manage your Watchlists

> Learn how to create custom watchlists, populate them with assets, and sync your preferences across the eToro ecosystem.

Watchlists are the primary way to organize and track groups of assets on eToro. By managing watchlists via the API, you can programmatically sync your external favorites, create custom sectors (e.g., "My AI Picks"), and control what appears on your main trading dashboard.

## 1. Get Your Watchlists

First, retrieve the list of watchlists currently associated with your account. This will provide you with the `watchlistId` needed for further management actions.

**Endpoint:** `GET /api/v1/watchlists`

<CodeGroup>
  ```bash cURL theme={null}
  curl -X GET "https://public-api.etoro.com/api/v1/watchlists" \
    -H "x-api-key: <YOUR_PUBLIC_KEY>" \
    -H "x-user-key: <YOUR_USER_KEY>" \
    -H "x-request-id: <UUID>"
  ```

  ```javascript JavaScript theme={null}
  fetch('https://public-api.etoro.com/api/v1/watchlists', {
      headers: {
          'x-api-key': '<YOUR_PUBLIC_KEY>',
          'x-user-key': '<YOUR_USER_KEY>',
          'x-request-id': 'your-uuid-here'
      }
  })
  .then(res => res.json())
  .then(console.log)
  .catch(console.error);
  ```

  ```python Python theme={null}
  import requests
  import uuid

  url = "https://public-api.etoro.com/api/v1/watchlists"
  headers = {
      "x-api-key": "<YOUR_PUBLIC_KEY>",
      "x-user-key": "<YOUR_USER_KEY>",
      "x-request-id": str(uuid.uuid4())
  }

  response = requests.get(url, headers=headers)
  print(response.json())
  ```
</CodeGroup>

> **Note:** You can use the `ensureBuiltinWatchlists` parameter to make sure system default lists are included in the response.

## 2. Create a New Watchlist

You can create a custom watchlist by specifying a name.

**Endpoint:** `POST /api/v1/watchlists`

| Parameter | Type  | Description                                                    |
| --------- | ----- | -------------------------------------------------------------- |
| `name`    | Query | The display name for your new watchlist (e.g., "Tech Stocks"). |

<CodeGroup>
  ```bash cURL theme={null}
  curl -X POST "https://public-api.etoro.com/api/v1/watchlists?name=Tech%20Stocks" \
    -H "x-api-key: <YOUR_PUBLIC_KEY>" \
    -H "x-user-key: <YOUR_USER_KEY>" \
    -H "x-request-id: <UUID>"
  ```

  ```javascript JavaScript theme={null}
  const name = 'Tech Stocks';
  const url = `https://public-api.etoro.com/api/v1/watchlists?name=${encodeURIComponent(name)}`;

  fetch(url, {
      method: 'POST',
      headers: {
          'x-api-key': '<YOUR_PUBLIC_KEY>',
          'x-user-key': '<YOUR_USER_KEY>',
          'x-request-id': 'your-uuid-here'
      }
  })
  .then(res => res.json())
  .then(console.log)
  .catch(console.error);
  ```

  ```python Python theme={null}
  import requests
  import uuid

  url = "https://public-api.etoro.com/api/v1/watchlists"
  params = {"name": "Tech Stocks"}
  headers = {
      "x-api-key": "<YOUR_PUBLIC_KEY>",
      "x-user-key": "<YOUR_USER_KEY>",
      "x-request-id": str(uuid.uuid4())
  }

  response = requests.post(url, params=params, headers=headers)
  print(response.json())
  ```
</CodeGroup>

## 3. Add Instruments to a Watchlist

Once a watchlist is created, you can add instruments to it using its `watchlistId`.

**Endpoint:** `POST /api/v1/watchlists/{watchlistId}/items`

<CodeGroup>
  ```bash cURL theme={null}
  curl -X POST "https://public-api.etoro.com/api/v1/watchlists/{watchlistId}/items" \
    -H "x-api-key: <YOUR_PUBLIC_KEY>" \
    -H "x-user-key: <YOUR_USER_KEY>" \
    -H "x-request-id: <UUID>" \
    -H "Content-Type: application/json" \
    -d '[1001, 1002]'
  ```

  ```javascript JavaScript theme={null}
  const watchlistId = '12345';
  const url = `https://public-api.etoro.com/api/v1/watchlists/${watchlistId}/items`;

  fetch(url, {
      method: 'POST',
      headers: {
          'x-api-key': '<YOUR_PUBLIC_KEY>',
          'x-user-key': '<YOUR_USER_KEY>',
          'x-request-id': 'your-uuid-here',
          'Content-Type': 'application/json'
      },
      body: JSON.stringify([1001, 1002])
  })
  .then(res => res.json())
  .then(console.log)
  .catch(console.error);
  ```

  ```python Python theme={null}
  import requests
  import uuid

  watchlist_id = "12345"
  url = f"https://public-api.etoro.com/api/v1/watchlists/{watchlist_id}/items"
  headers = {
      "x-api-key": "<YOUR_PUBLIC_KEY>",
      "x-user-key": "<YOUR_USER_KEY>",
      "x-request-id": str(uuid.uuid4()),
      "Content-Type": "application/json"
  }

  payload = [1001, 1002]

  # payload is a list of instrument IDs
  response = requests.post(url, json=payload, headers=headers)
  print(response.json())
  ```
</CodeGroup>

## 4. Set a Default Watchlist

The "Default" watchlist is the one that appears immediately when you log in.

**Endpoint:** `PUT /api/v1/watchlists/setUserSelectedUserDefault/{watchlistId}`

<CodeGroup>
  ```bash cURL theme={null}
  curl -X PUT "https://public-api.etoro.com/api/v1/watchlists/setUserSelectedUserDefault/12345" \
    -H "x-api-key: <YOUR_PUBLIC_KEY>" \
    -H "x-user-key: <YOUR_USER_KEY>" \
    -H "x-request-id: <UUID>"
  ```

  ```javascript JavaScript theme={null}
  const watchlistId = '12345';
  const url = `https://public-api.etoro.com/api/v1/watchlists/setUserSelectedUserDefault/${watchlistId}`;

  fetch(url, {
      method: 'PUT',
      headers: {
          'x-api-key': '<YOUR_PUBLIC_KEY>',
          'x-user-key': '<YOUR_USER_KEY>',
          'x-request-id': 'your-uuid-here'
      }
  })
  .then(res => res.json())
  .then(console.log)
  .catch(console.error);
  ```

  ```python Python theme={null}
  import requests
  import uuid

  watchlist_id = "12345"
  url = f"https://public-api.etoro.com/api/v1/watchlists/setUserSelectedUserDefault/{watchlist_id}"
  headers = {
      "x-api-key": "<YOUR_PUBLIC_KEY>",
      "x-user-key": "<YOUR_USER_KEY>",
      "x-request-id": str(uuid.uuid4())
  }

  response = requests.put(url, headers=headers)
  print(response.json())
  ```
</CodeGroup>

## 5. Delete a Watchlist

Remove a watchlist and all its contained items permanently.

**Endpoint:** `DELETE /api/v1/watchlists/{watchlistId}`

<CodeGroup>
  ```bash cURL theme={null}
  curl -X DELETE "https://public-api.etoro.com/api/v1/watchlists/12345" \
    -H "x-api-key: <YOUR_PUBLIC_KEY>" \
    -H "x-user-key: <YOUR_USER_KEY>" \
    -H "x-request-id: <UUID>"
  ```

  ```javascript JavaScript theme={null}
  const watchlistId = '12345';
  const url = `https://public-api.etoro.com/api/v1/watchlists/${watchlistId}`;

  fetch(url, {
      method: 'DELETE',
      headers: {
          'x-api-key': '<YOUR_PUBLIC_KEY>',
          'x-user-key': '<YOUR_USER_KEY>',
          'x-request-id': 'your-uuid-here'
      }
  })
  .then(res => res.json())
  .then(console.log)
  .catch(console.error);
  ```

  ```python Python theme={null}
  import requests
  import uuid

  watchlist_id = "12345"
  url = f"https://public-api.etoro.com/api/v1/watchlists/{watchlist_id}"
  headers = {
      "x-api-key": "<YOUR_PUBLIC_KEY>",
      "x-user-key": "<YOUR_USER_KEY>",
      "x-request-id": str(uuid.uuid4())
  }

  response = requests.delete(url, headers=headers)
  print(response.json())
  ```
</CodeGroup>
