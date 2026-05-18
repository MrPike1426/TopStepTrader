import sqlite3
import sys
from pathlib import Path

# Share the resolver with db_reader so the two scripts can't drift apart again.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from db_reader import default_db_path  # noqa: E402

db = default_db_path()
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
