#!/usr/bin/env python3
"""Query and manage listings in SQL Server LocalDB."""

import argparse
import subprocess
import sys

CONNECTION_STRING = r"Server=(localdb)\MSSQLLocalDB;Database=AIOMarketMaker;Trusted_Connection=True;TrustServerCertificate=True;"

def run_sql(query: str, params: dict = None) -> str:
    """Run SQL query using sqlcmd or pyodbc."""
    try:
        import pyodbc
        conn = pyodbc.connect(f"Driver={{ODBC Driver 17 for SQL Server}};{CONNECTION_STRING}")
        cursor = conn.cursor()
        if params:
            cursor.execute(query, list(params.values()))
        else:
            cursor.execute(query)

        if query.strip().upper().startswith("SELECT"):
            rows = cursor.fetchall()
            columns = [desc[0] for desc in cursor.description]
            result = []
            for row in rows:
                result.append(dict(zip(columns, row)))
            conn.close()
            return result
        else:
            affected = cursor.rowcount
            conn.commit()
            conn.close()
            return f"Rows affected: {affected}"
    except ImportError:
        # Fall back to sqlcmd
        print("pyodbc not installed, using sqlcmd...")
        cmd = ["sqlcmd", "-S", "(localdb)\\MSSQLLocalDB", "-d", "AIOMarketMaker", "-Q", query]
        result = subprocess.run(cmd, capture_output=True, text=True)
        return result.stdout or result.stderr

def get_listing(listing_id: str) -> None:
    """Get listing details by ID."""
    query = f"SELECT Id, ListingId, Title, ListingStatus, Price, Currency FROM Listings WHERE ListingId = '{listing_id}'"
    result = run_sql(query)

    if isinstance(result, list):
        if result:
            for row in result:
                print(f"ID: {row['Id']}")
                print(f"ListingId: {row['ListingId']}")
                print(f"Title: {row['Title']}")
                print(f"Status: {row['ListingStatus']}")
                print(f"Price: {row['Currency']} {row['Price']}")
        else:
            print(f"Listing {listing_id} not found in database")
    else:
        print(result)

def delete_listing(listing_id: str) -> None:
    """Delete a listing by ID."""
    # First show what we're deleting
    get_listing(listing_id)
    print()

    query = f"DELETE FROM Listings WHERE ListingId = '{listing_id}'"
    result = run_sql(query)
    print(f"Delete result: {result}")

def list_active(limit: int = 20) -> None:
    """List active listings."""
    query = f"SELECT TOP {limit} Id, ListingId, Title, ListingStatus FROM Listings WHERE ListingStatus = 'Active' ORDER BY Id DESC"
    result = run_sql(query)

    if isinstance(result, list):
        print(f"Active listings (top {limit}):\n")
        for row in result:
            title = row['Title'][:50] + '...' if len(row['Title']) > 50 else row['Title']
            print(f"  {row['Id']}: {row['ListingId']} - {title}")
    else:
        print(result)

def main():
    parser = argparse.ArgumentParser(description="Query AIOMarketMaker database")
    subparsers = parser.add_subparsers(dest="command", required=True)

    # get command
    get_parser = subparsers.add_parser("get", help="Get listing by ID")
    get_parser.add_argument("listing_id", help="eBay listing ID")

    # delete command
    del_parser = subparsers.add_parser("delete", help="Delete listing by ID")
    del_parser.add_argument("listing_id", help="eBay listing ID")

    # list command
    list_parser = subparsers.add_parser("list", help="List active listings")
    list_parser.add_argument("--limit", type=int, default=20, help="Max listings to show")

    args = parser.parse_args()

    if args.command == "get":
        get_listing(args.listing_id)
    elif args.command == "delete":
        delete_listing(args.listing_id)
    elif args.command == "list":
        list_active(args.limit)

if __name__ == "__main__":
    main()
