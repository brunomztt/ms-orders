"""Real integration checks against Compose: python scripts/smoke.py"""
import json
import os
import time
import unittest
import urllib.request
import urllib.error
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

ORDERS = os.getenv("ORDERS_URL", "http://localhost:8081")
CATALOG = os.getenv("CATALOG_URL", "http://localhost:8080")
NOTIFICATIONS = os.getenv("NOTIFICATIONS_URL", "http://localhost:8000")


def request(base, path, method="GET", body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base + path, data=data, method=method, headers={"Content-Type": "application/json"})
    try:
        response = urllib.request.urlopen(req, timeout=10)
    except urllib.error.HTTPError as exc:
        response = exc
    raw = response.read()
    return response.status, json.loads(raw) if raw else None


def wait_for(check, timeout=60):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = check()
        if result:
            return result
        time.sleep(1)
    raise AssertionError("Timed out waiting for state")


class IntegrationTests(unittest.TestCase):
    def product(self, quantity=10):
        code, product = request(CATALOG, "/api/catalog/products", "POST", {
            "sku": str(uuid4()), "name": "Ração Kitoca", "price": "12.50", "available_quantity": quantity})
        self.assertEqual(code, 201)
        return product["id"]

    def order(self, items):
        code, order = request(ORDERS, "/api/orders", "POST", {"userId": "tutor-test", "items": items})
        self.assertEqual(code, 201, order)
        return order

    def confirm(self, order):
        code, _ = request(ORDERS, f'/api/orders/{order["id"]}/confirm', "POST")
        self.assertIn(code, (200, 202))
        return wait_for(lambda: self.terminal(order["id"]))

    def terminal(self, order_id):
        code, order = request(ORDERS, f"/api/orders/{order_id}")
        self.assertEqual(code, 200)
        return order if order["status"] in ("CONFIRMED", "REJECTED") else None

    def available(self, product):
        return request(CATALOG, f"/api/catalog/products/{product}")[1]["available_quantity"]

    def test_01_crud_validation(self):
        self.assertEqual(request(ORDERS, "/health")[0], 200)
        self.assertEqual(request(ORDERS, "/api/orders", "POST", {"userId": "", "items": []})[0], 400)
        self.assertEqual(request(ORDERS, "/api/orders", "POST", {"userId": "a", "items": [{"productId": 1, "quantity": 0}]})[0], 400)
        duplicate = [{"productId": 1, "quantity": 1}, {"productId": 2, "quantity": 1}]
        self.assertEqual(request(ORDERS, "/api/orders", "POST", {"userId": "a", "items": duplicate})[0], 400)
        order = self.order([{"productId": 1, "quantity": 1}])
        path = f'/api/orders/{order["id"]}'
        self.assertEqual(request(ORDERS, path, "PUT", {"userId": "updated", "items": [{"productId": 2, "quantity": 3}]})[0], 200)
        self.assertEqual(request(ORDERS, path)[1]["userId"], "updated")
        self.assertEqual(request(ORDERS, path, "DELETE")[0], 204)
        self.assertEqual(request(ORDERS, path)[0], 404)
        self.assertEqual(request(ORDERS, "/api/orders?limit=101")[0], 400)

    def test_02_confirm_and_notification(self):
        p1 = self.product()
        order = self.order([{"productId": p1, "quantity": 2}])
        final = self.confirm(order)
        self.assertEqual(final["status"], "CONFIRMED")
        self.assertEqual(final["total"], 25)
        self.assertEqual(self.available(p1), 8)
        self.confirm(order)
        self.assertEqual(self.available(p1), 8)
        self.assertEqual(request(ORDERS, f'/api/orders/{order["id"]}', "DELETE")[0], 409)
        def notification():
            _, body = request(NOTIFICATIONS, "/api/notifications/user/tutor-test")
            return next((e for e in body["items"] if e.get("order_id") == order["id"] and e.get("event_type") == "order.confirmed"), None)
        event = wait_for(notification)
        self.assertTrue(event["event_id"])

    def test_03_reject_insufficient_stock(self):
        p1 = self.product(0)
        order = self.order([{"productId": p1, "quantity": 2}])
        self.assertEqual(self.confirm(order)["status"], "REJECTED")
        self.assertEqual(self.available(p1), 0)
        self.assertEqual(request(CATALOG, f'/api/catalog/reservations/{order["id"]}')[1], [])

    def test_04_concurrent_stock_and_idempotency(self):
        p = self.product(1)
        def reserve(order_id):
            return request(CATALOG, f"/api/catalog/products/{p}/reservations", "POST",
                {"order_id": order_id, "quantity": 1})[0]
        ids = [str(uuid4()), str(uuid4())]
        with ThreadPoolExecutor(2) as pool:
            results = list(pool.map(reserve, ids))
        self.assertEqual(sorted(results), [201, 409])
        self.assertEqual(reserve(ids[results.index(201)]), 201)
        self.assertEqual(self.available(p), 0)

if __name__ == "__main__":
    unittest.main(verbosity=2)
