> ## Documentation Index
> Fetch the complete documentation index at: https://api-portal.etoro.com/llms.txt
> Use this file to discover all available pages before exploring further.

# Find Instrument ID

> Learn how to resolve a ticker symbol to an eToro Instrument ID.

Most endpoints in the eToro Public API require a numeric `instrumentId` to identify the asset you wish to access, rather than the text-based ticker symbol (e.g., 'AAPL').

To bridge this gap, you must use the Search endpoint to resolve a symbol into its corresponding ID.

## The Resolution Process

Resolving a symbol involves sending a specific query to the market data search endpoint and parsing the result to find the matching instrument record.

### 1. Endpoint

Use the **Search** endpoint to query for the instrument.

`GET https://public-api.etoro.com/api/v1/market-data/search`

### 2. Query Parameters

While the search endpoint accepts general search text, the most precise way to find a specific ID is to filter by the `internalSymbolFull` field.

| Parameter            | Value  | Description                                     |
| :------------------- | :----- | :---------------------------------------------- |
| `internalSymbolFull` | `AAPL` | The specific ticker symbol you want to resolve. |

### 3. Header Requirements

Remember to include your authentication headers with every request.

* `x-api-key`
* `x-user-key`
* `x-request-id`

## Examples

<CodeGroup>
  ```bash cURL theme={null}
  curl -X GET "https://public-api.etoro.com/api/v1/market-data/search?internalSymbolFull=AAPL" \
    -H "x-api-key: <YOUR_PUBLIC_KEY>" \
    -H "x-user-key: <YOUR_USER_KEY>" \
    -H "x-request-id: <UUID>"
  ```

  ```javascript JavaScript theme={null}
  const symbol = 'AAPL';
  const url = `https://public-api.etoro.com/api/v1/market-data/search?internalSymbolFull=${symbol}`;

  fetch(url, options)
      .then(res => res.json())
      // Find the exact match in the returned items list
      .then(res => {
          const instrument = res.items.find(i => i.internalSymbolFull === symbol);
          console.log("Instrument ID:", instrument.instrumentId);
      })
      .catch(err => console.error(err));
  ```

  ```python Python theme={null}
  import requests
  import uuid

  symbol = "AAPL"
  url = "https://public-api.etoro.com/api/v1/market-data/search"

  # Use internalSymbolFull to filter specifically for the symbol
  params = {
      "internalSymbolFull": symbol
  }

  headers = {
      "x-api-key": "<YOUR_PUBLIC_KEY>",
      "x-user-key": "<YOUR_USER_KEY>",
      "x-request-id": str(uuid.uuid4())
  }

  response = requests.get(url, headers=headers, params=params)

  if response.status_code == 200:
      data = response.json()
      # Find the exact match in the returned items list
      instrument = next((item for item in data['items'] if item['internalSymbolFull'] == symbol), None)
      
      if instrument:
          print(f"Instrument ID: {instrument['instrumentId']}")
      else:
          print("Instrument not found")
  else:
      print(f"Error: {response.status_code}")
  ```
</CodeGroup>

### Best Practices

1. **Verify the Match:** The search might return partial matches or related assets. Always verify that the `internalSymbolFull` property of the returned item exactly matches your requested symbol.

2. **Cache your IDs**: Instrument IDs are immutable: They never change, even if a company rebrands or changes its ticker symbol. While symbols rarely change, relying on the permanent instrumentId ensures your application remains robust against such events. We strongly recommend fetching these IDs once and caching the mapping (e.g., AAPL -> 1001) locally.
