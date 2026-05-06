import sqlite3
import os
from pathlib import Path

db = Path(os.environ.get('LOCALAPPDATA', '')) / 'TopStepTrader' / 'debug_trades.db'
conn = sqlite3.connect(str(db))
cursor = conn.cursor()

# First, check the schema
cursor.execute("PRAGMA table_info(DebugTrades)")
columns = cursor.fetchall()
print("DebugTrades columns:")
for col in columns:
    print(f"  {col}")
print()

# Query M2K trades
cursor.execute("""
    SELECT TradeId, Instrument, Direction, EntryTime, ClosedAt, RealisedPnLDollars 
    FROM DebugTrades 
    WHERE Instrument = 'M2K' AND EntryTime LIKE '2026-05-05%'
    ORDER BY EntryTime DESC
""")
rows = cursor.fetchall()

print(f"Found {len(rows)} M2K trades on 2026-05-05:")
for row in rows:
    print(f"  {row}")

conn.close()
