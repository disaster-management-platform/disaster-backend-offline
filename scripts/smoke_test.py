"""Prueba real Gateway -> API -> BLL -> DAL -> PostgreSQL, con base temporal.

Ejecutar desde la raíz:
dotnet build && dotnet build ../disaster-api-gateway/DisasterLocation.Gateway.sln
python3 scripts/smoke_test.py
No modifica los datos de la aplicación ni usa sus puertos de desarrollo.
"""
import json
from concurrent.futures import ThreadPoolExecutor
from http.client import HTTPConnection
import os
from pathlib import Path
import socket
import psycopg2
import psycopg2.sql
from contextlib import closing, contextmanager
import subprocess
import tempfile
import time
import uuid
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1]


@contextmanager
def test_database():
    settings = psycopg2.extensions.parse_dsn(os.environ["TEST_POSTGRES_DSN"])
    if not settings.get("host", "").startswith("/"):
        raise ValueError("Las pruebas requieren un socket PostgreSQL local, nunca Supabase.")
    name = "platform_test_" + uuid.uuid4().hex
    with closing(psycopg2.connect(**settings)) as admin:
        admin.autocommit = True
        with admin.cursor() as cursor:
            cursor.execute(psycopg2.sql.SQL("CREATE DATABASE {}").format(psycopg2.sql.Identifier(name)))
        try:
            settings["dbname"] = name
            settings["options"] = "-csearch_path=disasters,reconstruction,public"
            yield psycopg2.extensions.make_dsn(**settings)
        finally:
            with admin.cursor() as cursor:
                cursor.execute(psycopg2.sql.SQL("DROP DATABASE {} WITH (FORCE)").format(psycopg2.sql.Identifier(name)))


def postgres_connection_string(database):
    settings = psycopg2.extensions.parse_dsn(database)
    def escape(value):
        return '"' + value.replace('"', '""') + '"'
    return ";".join(key + "=" + escape(settings[source]) for key, source in
        [("Host", "host"), ("Port", "port"), ("Database", "dbname"), ("Username", "user"), ("Password", "password")]
        if source in settings)


def migrate_database(database, log):
    project = ROOT / "DisasterLocation"
    subprocess.run(["dotnet", str(project / "bin/Debug/net10.0/DisasterLocation.dll"), "--Database:Migrate=true"],
        cwd=project, env={**os.environ, "ConnectionStrings__Disasters": postgres_connection_string(database)},
        stdout=log, stderr=log, check=True, timeout=60)


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def request(base, path="/api/desastres", method="GET", data=None, headers=None):
    body = None if data is None else json.dumps(data).encode()
    request_headers = {"Content-Type": "application/json"} if body else {}
    request_headers.update(headers or {})
    req = Request(base + path, data=body, method=method, headers=request_headers)
    try:
        response = urlopen(req, timeout=5)
    except HTTPError as error:
        response = error
    with response:
        raw = response.read()
        content = json.loads(raw) if raw and "json" in response.headers.get("Content-Type", "") else raw
        return response.status, response.headers, content


def wait_ready(base, path, process):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise AssertionError("El proceso terminó antes de iniciar. Revisa los logs.")
        try:
            if request(base, path)[0] == 200:
                return
        except (URLError, TimeoutError):
            pass
        time.sleep(.15)
    raise AssertionError("La aplicación no inició en 30 segundos.")


def start(project, port, env, log):
    root = ROOT.parent / "disaster-api-gateway" if project == "DisasterLocation.Gateway" else ROOT
    dll = root / project / "bin/Debug/net10.0" / f"{project}.dll"
    return subprocess.Popen(
        ["dotnet", str(dll), "--urls", f"http://127.0.0.1:{port}"],
        cwd=root / project, env={**os.environ, **env}, stdout=log, stderr=log)


def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


def verify_idempotency(base, payload):
    key = str(uuid.uuid4())
    operation = {"Idempotency-Key": key, "User-Agent": "okhttp/Android"}
    status, headers, saved = request(base, method="POST", data=payload, headers=operation)
    assert status == 201 and headers["Location"] == f'/api/desastres/{saved["id"]}'
    assert set(saved) == set(payload) | {"id", "radio", "phase", "version", "disasterTypeId", "startedAt", "registeredAt", "updatedAt", "attentionLevel"}
    assert saved["radio"] is None and saved["phase"] == "Registered" and saved["version"] == 1
    for retry_key in [key, key.upper()]:
        status, _, replay = request(base, method="POST", data=payload,
            headers={"Idempotency-Key": retry_key})
        assert status == 200 and replay == saved, (status, replay)
    normalized = {**payload, "nombre": payload["nombre"].strip()}
    assert request(base, method="POST", data=normalized, headers=operation)[0] == 200
    for change in [{"nombre": "Otro nombre"}, {"ubicacion": "Cali"}, {"severidad": 2},
                   {"descripcion": "Otra descripción"}, {"latitud": 1}, {"longitud": 1}]:
        status, _, problem = request(base, method="POST", data={**payload, **change}, headers=operation)
        assert status == 409 and problem["status"] == 409, (change, problem)
    for invalid_key in ["", "no-es-uuid", str(uuid.UUID(int=0)), key + "," + key, key.replace("-", "")]:
        status, _, problem = request(base, method="POST", data=payload,
            headers={"Idempotency-Key": invalid_key})
        assert status == 400 and "Idempotency-Key" in problem["errors"], (invalid_key, problem)
    address = urlsplit(base)
    connection = HTTPConnection(address.hostname, address.port, timeout=5)
    try:
        body = json.dumps(payload).encode()
        connection.putrequest("POST", "/api/desastres")
        connection.putheader("Content-Type", "application/json")
        connection.putheader("Content-Length", str(len(body)))
        connection.putheader("Idempotency-Key", key)
        connection.putheader("Idempotency-Key", key)
        connection.endheaders(body)
        response = connection.getresponse()
        problem = json.loads(response.read())
        assert response.status == 400 and "Idempotency-Key" in problem["errors"]
    finally:
        connection.close()
    # Una clave conocida tampoco permite omitir la validación del cuerpo.
    assert request(base, method="POST", data={**payload, "severidad": 0}, headers=operation)[0] == 400

    concurrent_key = str(uuid.uuid4())
    def send(body):
        return request(base, method="POST", data=body, headers={"Idempotency-Key": concurrent_key})

    with ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(send, [payload] * 8))
    assert sorted(result[0] for result in results) == [200] * 7 + [201], results
    assert len({result[2]["id"] for result in results}) == 1

    concurrent_key = str(uuid.uuid4())
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(send, [payload, {**payload, "nombre": "Contenido diferente"}]))
    assert sorted(result[0] for result in results) == [201, 409], results

    # El contenido por sí solo no identifica una operación.
    assert request(base, method="POST", data=payload,
        headers={"Idempotency-Key": str(uuid.uuid4())})[0] == 201
    assert len(request(base)[2]) == 6, "Cada operación debe crear exactamente un registro"
    return operation, saved


def main():
    processes = []
    with tempfile.TemporaryDirectory(prefix="disasters-test-") as folder, test_database() as database:
        api_port, gateway_port = free_port(), free_port()
        api_url = f"http://127.0.0.1:{api_port}"
        gateway_url = f"http://127.0.0.1:{gateway_port}"
        api_env = {"ConnectionStrings__Disasters": postgres_connection_string(database)}
        with open(Path(folder) / "processes.log", "w+") as log:
            try:
                migrate_database(database, log)
                backend = start("DisasterLocation", api_port, api_env, log)
                processes.append(backend)
                wait_ready(api_url, "/api/desastres", backend)
                gateway = start("DisasterLocation.Gateway", gateway_port, {
                    "ReverseProxy__Clusters__disasters-api__Destinations__local__Address": api_url + "/",
                    "Cors__AllowedOrigins__0": "http://localhost:3000"
                }, log)
                processes.append(gateway)
                wait_ready(gateway_url, "/", gateway)
                assert request(gateway_url)[2] == []
                for asset in ["/", "/app.js", "/styles.css", "/lib/leaflet/leaflet.js", "/lib/leaflet/leaflet.css"]:
                    status, _, body = request(gateway_url, asset)
                    assert status == 200 and body, asset

                payload = {
                    "nombre": "  Inundación Bogotá  ", "ubicacion": "Bogotá, Colombia",
                    "severidad": 4, "descripcion": "Desbordamiento <script>alert(1)</script>",
                    "latitud": 4.711, "longitud": -74.0721
                }
                status, headers, saved = request(gateway_url, method="POST", data=payload,
                    headers={"User-Agent": "WebClient/1.0"})
                assert status == 201 and saved["nombre"] == "Inundación Bogotá"
                assert saved["id"] > 0 and saved["descripcion"] == payload["descripcion"]
                location = headers["Location"]
                assert location == f'/api/desastres/{saved["id"]}'
                assert request(gateway_url, location)[2] == saved
                assert request(api_url)[2] == request(gateway_url)[2] == [saved]
                mobile = {**payload, "nombre": "Reporte desde móvil", "severidad": 1,
                          "latitud": 0, "longitud": 0}
                assert request(gateway_url, method="POST", data=mobile,
                    headers={"User-Agent": "MobileClient/1.0"})[0] == 201
                assert len(request(gateway_url)[2]) == 2

                invalid_cases = [
                    ({"severidad": 0}, "severidad"), ({"severidad": 6}, "severidad"),
                    ({"severidad": None}, "severidad"), ({"nombre": "  "}, "nombre"),
                    ({"ubicacion": None}, "ubicacion"), ({"descripcion": ""}, "descripcion"),
                    ({"nombre": "a" * 121}, "nombre"), ({"ubicacion": "a" * 201}, "ubicacion"),
                    ({"descripcion": "a" * 2001}, "descripcion"),
                    ({"latitud": 91}, "latitud"), ({"latitud": -91}, "latitud"),
                    ({"longitud": 181}, "longitud"), ({"longitud": -181}, "longitud"),
                    ({"latitud": None}, "latitud"), ({"longitud": None}, "longitud")
                ]
                for change, field in invalid_cases:
                    status, headers, problem = request(gateway_url, method="POST", data={**payload, **change})
                    assert status == 400 and field in problem["errors"], (change, problem)
                    assert "application/problem+json" in headers["Content-Type"]
                for invalid in [{}, {**payload, "severidad": 1.5}, {**payload, "latitud": "abc"}]:
                    assert request(gateway_url, method="POST", data=invalid)[0] == 400
                assert request(gateway_url, "/api/desastres/999999")[0] == 404
                assert request(gateway_url, method="DELETE")[0] == 405
                assert len(request(gateway_url)[2]) == 2, "Los datos inválidos no se deben guardar"

                # El gateway conserva la respuesta de la API, incluso los errores.
                bad = {**payload, "severidad": 8}
                gateway_problem = request(gateway_url, method="POST", data=bad)[2]
                api_problem = request(api_url, method="POST", data=bad)[2]
                # Problem Details incluye un traceId distinto en cada solicitud.
                for field in ("status", "title", "type", "errors"):
                    assert gateway_problem[field] == api_problem[field]
                status, headers, _ = request(gateway_url, method="OPTIONS", headers={
                    "Origin": "http://localhost:3000", "Access-Control-Request-Method": "POST",
                    "Access-Control-Request-Headers": "content-type,idempotency-key"
                })
                assert status == 204 and headers["Access-Control-Allow-Origin"] == "http://localhost:3000"
                assert "idempotency-key" in headers["Access-Control-Allow-Headers"].lower()

                operation, idempotent = verify_idempotency(gateway_url, payload)

                replica_port = free_port()
                replica_url = f"http://127.0.0.1:{replica_port}"
                replica = start("DisasterLocation", replica_port, api_env, log)
                processes.append(replica)
                wait_ready(replica_url, "/api/desastres", replica)
                assert request(replica_url)[2] == request(gateway_url)[2]
                shared_key = str(uuid.uuid4())
                with ThreadPoolExecutor(max_workers=2) as pool:
                    results = list(pool.map(lambda base: request(base, method="POST", data=payload,
                        headers={"Idempotency-Key": shared_key}), [gateway_url, replica_url]))
                assert sorted(result[0] for result in results) == [200, 201], results
                assert results[0][2] == results[1][2]
                stop(replica)

                stop(backend)
                assert request(gateway_url)[0] == 502, "El gateway debe indicar que la API no está disponible"
                backend = start("DisasterLocation", api_port, api_env, log)
                processes.append(backend)
                wait_ready(api_url, "/api/desastres", backend)
                assert len(request(gateway_url)[2]) == 7, "Los datos deben persistir tras reiniciar"
                status, _, replay = request(gateway_url, method="POST", data=payload, headers=operation)
                assert status == 200 and replay == idempotent, "El reintento tras reiniciar debe recuperar el mismo ID"
                with closing(psycopg2.connect(database)) as connection, connection.cursor() as cursor:
                    cursor.execute('SELECT COUNT(*) FROM "Desastres"')
                    assert cursor.fetchone()[0] == 7
                    cursor.execute('SELECT COUNT(*) FROM disasters."__EFMigrationsHistory_Disaster"')
                    assert cursor.fetchone()[0] == 4
                    try:
                        cursor.execute('UPDATE "Desastres" SET "ClientRequestId" = %s', (operation["Idempotency-Key"].upper(),))
                    except psycopg2.IntegrityError:
                        connection.rollback()
                    else:
                        raise AssertionError("Falta el índice único de operación en PostgreSQL")
                    try:
                        cursor.execute('UPDATE "Desastres" SET "Severidad" = 6')
                    except psycopg2.IntegrityError:
                        connection.rollback()
                    else:
                        raise AssertionError("Falta la restricción de severidad en PostgreSQL")
                assert request(gateway_url, method="POST", data={**payload, "radio": None}, headers=operation)[0] == 200
                assert request(gateway_url, method="POST", data={**payload, "radio": 500}, headers=operation)[0] == 409
                radio_key = {"Idempotency-Key": str(uuid.uuid4())}
                radio_body = {**payload, "radio": 500}
                status, _, with_radius = request(gateway_url, method="POST", data=radio_body, headers=radio_key)
                assert status == 201 and with_radius["radio"] == 500
                assert request(gateway_url, method="POST", data=radio_body, headers=radio_key)[0] == 200
                for conflicting in [payload, {**payload, "radio": None}, {**payload, "radio": 501}]:
                    assert request(gateway_url, method="POST", data=conflicting, headers=radio_key)[0] == 409
                for invalid_radius in [0, -1, 1_000_001, "invalid"]:
                    assert request(gateway_url, method="POST", data={**payload, "radio": invalid_radius})[0] == 400
                assert request(gateway_url, f"/api/desastres/{with_radius['id']}")[2] == with_radius
                assert request(gateway_url, f"/api/desastres/{with_radius['id']}/fase", "PUT",
                    {"phase": "Response", "expectedVersion": 1})[0] == 401
                print("OK: web/móvil, validación, 201/200/409, concurrencia, gateway, CORS, índice único, reinicio y migraciones sin pérdida de datos ni UUID.")
            except Exception:
                log.flush()
                log.seek(0)
                print(log.read()[-10000:])
                raise
            finally:
                for process in reversed(processes):
                    stop(process)


def benchmark():
    import threading
    import statistics
    import math
    from http.client import HTTPResponse
    rows = []
    upload_samples = {"individual": [], "batch": []}
    read_samples = {"individual": [], "batch": []}
    operations = [{"clientRequestId": str(uuid.uuid4()), "disaster": {
        "nombre": f"Medición {index}", "ubicacion": "Bogotá", "severidad": 4,
        "descripcion": "Mismo contenido en ambos protocolos", "latitud": 4.7, "longitud": -74.0, "radio": 1234.75}}
        for index in range(130)]

    class MeasuredConnection(HTTPConnection):
        def __init__(self, port):
            super().__init__("127.0.0.1", port, timeout=35)
            self.transferred = 0
            owner = self
            class Reader:
                def __init__(self, stream): self.stream = stream
                def read(self, *args):
                    value = self.stream.read(*args); owner.transferred += len(value); return value
                def readline(self, *args):
                    value = self.stream.readline(*args); owner.transferred += len(value); return value
                def __getattr__(self, name): return getattr(self.stream, name)
            class Socket:
                def __init__(self, sock): self.sock = sock
                def makefile(self, *args, **kwargs): return Reader(self.sock.makefile(*args, **kwargs))
            self.response_class = lambda sock, **kwargs: HTTPResponse(Socket(sock), **kwargs)
        def send(self, data):
            self.transferred += len(data)
            return super().send(data)
        def call(self, path, method="GET", body=None, headers=None):
            encoded = None if body is None else json.dumps(body).encode()
            values = {"Content-Type": "application/json"} if encoded else {}
            values.update(headers or {})
            start = time.perf_counter(); before = self.transferred
            self.request(method, path, body=encoded, headers=values)
            response = self.getresponse(); raw = response.read()
            return response.status, json.loads(raw) if raw else None, (time.perf_counter()-start)*1000, self.transferred-before

    def percentiles(values):
        values = sorted(values)
        return {str(p): values[min(len(values)-1, max(0, math.ceil(len(values)*p/100)-1))] for p in [50, 95, 99]}

    for trial in range(3):
        for mode in ["individual", "batch"]:
            with test_database() as database, tempfile.TemporaryFile(mode="w+") as log:
                processes = []; finished = threading.Event(); critical = []; errors = []; connections = []; phase = []
                clients = []
                try:
                    migrate_database(database, log)
                    a, g = free_port(), free_port()
                    api = start("DisasterLocation", a, {"ConnectionStrings__Disasters": postgres_connection_string(database),
                        "Security__LocalDemo": "true", "ASPNETCORE_ENVIRONMENT": "Development"}, log); processes.append(api); wait_ready(f"http://127.0.0.1:{a}", "/api/desastres", api)
                    gateway = start("DisasterLocation.Gateway", g, {"ReverseProxy__Clusters__disasters-api__Destinations__local__Address": f"http://127.0.0.1:{a}/"}, log)
                    processes.append(gateway); wait_ready(f"http://127.0.0.1:{g}", "/health", gateway)
                    client = MeasuredConnection(g); clients.append(client)
                    status, seed, _, _ = client.call("/api/desastres", "POST", {**operations[0]["disaster"], "nombre": "Crítico"})
                    assert status == 201
                    warm = {"clientRequestId": str(uuid.uuid4()), "disaster": {**operations[0]["disaster"], "nombre": "Calentamiento"}}
                    status, warm_result, _, _ = client.call("/api/desastres/batch", "POST", {"operations": [warm]})
                    assert status == 200
                    warm_id = warm_result["results"][0]["disaster"]["id"]
                    assert client.call(f"/api/desastres/{warm_id}/fase", "PUT", {"phase": "Assessment", "expectedVersion": 1})[0] == 200
                    assert client.call("/api/desastres/sync?limit=1")[0] == 200
                    assert client.call("/api/desastres")[0] == 200
                    assert client.call(f"/api/desastres/{seed['id']}")[0] == 200
                    def monitor():
                        connection = psycopg2.connect(os.environ["TEST_POSTGRES_DSN"]); connection.autocommit = True
                        try:
                            with connection.cursor() as cursor:
                                while not finished.is_set():
                                    cursor.execute("SELECT count(*) FROM pg_stat_activity WHERE datname=%s",
                                        (psycopg2.extensions.parse_dsn(database)["dbname"],))
                                    connections.append(cursor.fetchone()[0]); finished.wait(.01)
                        finally: connection.close()
                    def interactive():
                        reader = MeasuredConnection(g)
                        try:
                            while not finished.is_set():
                                status, _, elapsed, _ = reader.call(f"/api/desastres/{seed['id']}")
                                critical.append(elapsed)
                                if status != 200: errors.append(status)
                                finished.wait(.005)
                        except Exception as error: errors.append(type(error).__name__)
                        finally: reader.close()
                    def change_phase():
                        writer = MeasuredConnection(g)
                        try:
                            time.sleep(.02)
                            status, _, elapsed, _ = writer.call(f"/api/desastres/{seed['id']}/fase", "PUT",
                                {"phase": "Assessment", "expectedVersion": 1, "reason": "Medición local autorizada"})
                            phase.append(elapsed)
                            if status != 200: errors.append(status)
                        except Exception as error: errors.append(type(error).__name__)
                        finally: writer.close()
                    threads = [threading.Thread(target=target) for target in [monitor, interactive, change_phase]]
                    for thread in threads: thread.start()
                    latencies = []; transferred = 0; upload_started = time.perf_counter()
                    chunks = [operations[i:i+50] for i in range(0,130,50)] if mode == "batch" else [[item] for item in operations]
                    for chunk in chunks:
                        if mode == "batch":
                            status, result, elapsed, count = client.call("/api/desastres/batch", "POST", {"operations": chunk})
                            assert status == 200 and all(item["status"] == "Created" for item in result["results"])
                        else:
                            status, _, elapsed, count = client.call("/api/desastres", "POST", chunk[0]["disaster"],
                                {"Idempotency-Key": chunk[0]["clientRequestId"]})
                            assert status == 201
                        latencies.append(elapsed); transferred += count
                    duration = time.perf_counter()-upload_started
                    finished.set()
                    for thread in threads: thread.join(timeout=10); assert not thread.is_alive()
                    assert not errors, errors
                    initial_bytes = 0; initial_requests = 0; data = []; cursor = None
                    while True:
                        path = "/api/desastres" if mode == "individual" else "/api/desastres/sync" + ("?cursor=" + cursor if cursor else "")
                        status, result, _, count = client.call(path); assert status == 200
                        initial_requests += 1; initial_bytes += count
                        data.extend(result if mode == "individual" else result["items"])
                        if mode == "individual": break
                        cursor = result["nextCursor"]
                        if not result["hasMore"]: break
                    assert len(data) == 132
                    changed = sorted((item for item in data if item["id"] not in [seed["id"], warm_id]), key=lambda item: item["id"])[:5]
                    for item in changed:
                        body = {key: item[key] for key in ["nombre","ubicacion","severidad","descripcion","latitud","longitud","radio","disasterTypeId","startedAt","attentionLevel"]}
                        body.update(expectedVersion=item["version"], radio=8765.25)
                        assert client.call(f"/api/desastres/{item['id']}", "PUT", body)[0] == 200
                    path = "/api/desastres" if mode == "individual" else "/api/desastres/sync?cursor="+cursor
                    status, delta, _, delta_bytes = client.call(path); assert status == 200
                    downloaded = delta if mode == "individual" else delta["items"]
                    assert len(downloaded) == (132 if mode == "individual" else 5)
                    if mode == "batch": cursor = delta["nextCursor"]
                    status, unchanged, _, unchanged_bytes = client.call("/api/desastres" if mode == "individual" else "/api/desastres/sync?cursor="+cursor)
                    assert status == 200
                    if mode == "batch": assert unchanged["items"] == []
                    row = {"trial": trial+1, "mode": mode, "records": 130, "uploadRequests": len(chunks),
                        "uploadHttpBytes": transferred, "recordsPerSecond": 130/duration, "requestLatencyMs": percentiles(latencies),
                        "criticalReadLatencyMs": percentiles(critical), "criticalReadSamples": len(critical),
                        "criticalPhaseChangeMs": phase[0], "errors": len(errors), "peakPostgresConnections": max(connections),
                        "initialDownloadRequests": initial_requests, "initialDownloadHttpBytes": initial_bytes,
                        "incrementalDownloadRecords": len(downloaded), "incrementalDownloadHttpBytes": delta_bytes,
                        "unchangedDownloadHttpBytes": unchanged_bytes}
                    upload_samples[mode].extend(latencies); read_samples[mode].extend(critical)
                    rows.append(row); print(json.dumps(row), flush=True)
                finally:
                    finished.set()
                    for client in clients: client.close()
                    for process in reversed(processes): stop(process)
    summary = {}
    for mode in ["individual", "batch"]:
        summary[mode] = {key: (max if key == "peakPostgresConnections" else statistics.mean)(
            row[key] for row in rows if row["mode"] == mode) for key in
            ["uploadRequests","uploadHttpBytes","recordsPerSecond","peakPostgresConnections",
             "initialDownloadHttpBytes","incrementalDownloadHttpBytes","unchangedDownloadHttpBytes"]}
        summary[mode].update(requestLatencyMs=percentiles(upload_samples[mode]), requestSamples=len(upload_samples[mode]),
            criticalReadLatencyMs=percentiles(read_samples[mode]), criticalReadSamples=len(read_samples[mode]),
            criticalPhaseChangeMs=[row["criticalPhaseChangeMs"] for row in rows if row["mode"] == mode], errors=0)
    print(json.dumps({"summary": summary}), flush=True)


if __name__ == "__main__":
    import sys
    benchmark() if "--benchmark" in sys.argv else main()
