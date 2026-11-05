"""Prueba real Gateway -> API -> BLL -> DAL -> SQLite, con base temporal.

Ejecutar desde la raíz: dotnet build && python3 scripts/smoke_test.py
No modifica los datos de la aplicación ni usa sus puertos de desarrollo.
"""
import json
from concurrent.futures import ThreadPoolExecutor
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import tempfile
import time
import uuid
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]


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
    dll = ROOT / project / "bin/Debug/net10.0" / f"{project}.dll"
    return subprocess.Popen(
        ["dotnet", str(dll), "--urls", f"http://127.0.0.1:{port}"],
        cwd=ROOT / project, env={**os.environ, **env}, stdout=log, stderr=log)


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
    assert set(saved) == set(payload) | {"id"}, "El JSON debe conservar el contrato de Android"
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


def verify_schema_upgrade(folder, log, processes):
    database = Path(folder) / "legacy.db"
    with sqlite3.connect(database) as connection:
        connection.executescript('''
            CREATE TABLE Desastres (
                Id INTEGER NOT NULL CONSTRAINT PK_Desastres PRIMARY KEY AUTOINCREMENT,
                Nombre TEXT NOT NULL, Ubicacion TEXT NOT NULL, Severidad INTEGER NOT NULL,
                Descripcion TEXT NOT NULL, Latitud REAL NOT NULL, Longitud REAL NOT NULL,
                CONSTRAINT CK_Desastres_Severidad CHECK (Severidad BETWEEN 1 AND 5),
                CONSTRAINT CK_Desastres_Latitud CHECK (Latitud BETWEEN -90 AND 90),
                CONSTRAINT CK_Desastres_Longitud CHECK (Longitud BETWEEN -180 AND 180)
            );
            INSERT INTO Desastres (Nombre, Ubicacion, Severidad, Descripcion, Latitud, Longitud)
            VALUES ('Histórico 1', 'Bogotá', 1, 'Antes de la actualización', 0, 0),
                   ('Histórico 2', 'Cali', 2, 'Antes de la actualización', 1, 1);
        ''')
    port = free_port()
    base = f"http://127.0.0.1:{port}"
    payload = {"nombre": "Reporte nuevo", "ubicacion": "Bogotá", "severidad": 3,
               "descripcion": "Después de la actualización", "latitud": 4.711, "longitud": -74.0721}
    operation = {"Idempotency-Key": str(uuid.uuid4())}
    saved = None
    for attempt in range(2):
        backend = start("DisasterLocation", port,
            {"ConnectionStrings__Disasters": f"Data Source={database}"}, log)
        processes.append(backend)
        wait_ready(base, "/api/desastres", backend)
        records = request(base)[2]
        historical = [row for row in records if row["id"] in (1, 2)]
        assert {row["nombre"] for row in historical} == {"Histórico 1", "Histórico 2"}
        assert len(records) == 2 + attempt
        status, _, replay = request(base, method="POST", data=payload, headers=operation)
        if attempt == 0:
            assert status == 201 and replay["id"] == 3
            saved = replay
        else:
            assert status == 200 and replay == saved
        stop(backend)
    with sqlite3.connect(database) as connection:
        assert connection.execute("SELECT COUNT(*) FROM Desastres WHERE OperationId IS NULL").fetchone()[0] == 2
        assert any(row[1] == "IX_Desastres_OperationId" and row[2] == 1
                   for row in connection.execute("PRAGMA index_list('Desastres')"))


def main():
    processes = []
    with tempfile.TemporaryDirectory(prefix="disasters-test-") as folder:
        database = Path(folder) / "test.db"
        api_port, gateway_port = free_port(), free_port()
        api_url = f"http://127.0.0.1:{api_port}"
        gateway_url = f"http://127.0.0.1:{gateway_port}"
        api_env = {"ConnectionStrings__Disasters": f"Data Source={database}"}
        with open(Path(folder) / "processes.log", "w+") as log:
            try:
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

                stop(backend)
                assert request(gateway_url)[0] == 502, "El gateway debe indicar que la API no está disponible"
                backend = start("DisasterLocation", api_port, api_env, log)
                processes.append(backend)
                wait_ready(api_url, "/api/desastres", backend)
                assert len(request(gateway_url)[2]) == 6, "Los datos deben persistir tras reiniciar"
                status, _, replay = request(gateway_url, method="POST", data=payload, headers=operation)
                assert status == 200 and replay == idempotent, "El reintento tras reiniciar debe recuperar el mismo ID"
                with sqlite3.connect(database) as connection:
                    assert connection.execute("SELECT COUNT(*) FROM Desastres").fetchone()[0] == 6
                    try:
                        connection.execute("UPDATE Desastres SET OperationId = ?", (operation["Idempotency-Key"].upper(),))
                    except sqlite3.IntegrityError:
                        connection.rollback()
                    else:
                        raise AssertionError("Falta el índice único de operación en SQLite")
                    try:
                        connection.execute("UPDATE Desastres SET Severidad = 6")
                    except sqlite3.IntegrityError:
                        pass
                    else:
                        raise AssertionError("Falta la restricción de severidad en SQLite")
                verify_schema_upgrade(folder, log, processes)
                print("OK: web/móvil, validación, 201/200/409, concurrencia, gateway, CORS, índice único, reinicio y actualización sin pérdida de datos.")
            except Exception:
                log.flush()
                log.seek(0)
                print(log.read()[-10000:])
                raise
            finally:
                for process in reversed(processes):
                    stop(process)


if __name__ == "__main__":
    main()
