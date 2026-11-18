"""Small real PostgreSQL/RabbitMQ ingestion recovery scenario; local resources only."""
import base64
from contextlib import closing
import json
import subprocess
import tempfile
import sys
import time
import uuid
from urllib.request import Request, urlopen
import psycopg2
from smoke_test import test_database, migrate_database, postgres_connection_string, free_port, start, wait_ready, request


def main():
    vhost = 'disaster_ingestion_test_' + uuid.uuid4().hex
    def broker(path, method='GET', body=None):
        req = Request('http://127.0.0.1:15672/api/' + path, method=method,
            data=json.dumps(body).encode() if body is not None else None,
            headers={'Authorization': 'Basic ' + base64.b64encode(b'guest:guest').decode(), 'Content-Type': 'application/json'})
        with urlopen(req, timeout=10) as response:
            data = response.read()
            return json.loads(data) if data else None
    processes = []
    broker('vhosts/' + vhost, 'PUT', {})
    try:
        broker('permissions/' + vhost + '/guest', 'PUT', {'configure':'.*','read':'.*','write':'.*'})
        with test_database() as database:
            log = tempfile.TemporaryFile(mode='w+')
            migrate_database(database, log)
            env = {'ConnectionStrings__Disasters':postgres_connection_string(database), 'Ingestion__VirtualHost':vhost,
                   'Security__LocalDemo':'true','ASPNETCORE_ENVIRONMENT':'Development'}
            def launch(**settings):
                port=free_port(); process=start('DisasterLocation',port,{**env,**settings},log);processes.append(process)
                base=f'http://127.0.0.1:{port}';wait_ready(base,'/api/desastres',process);return base
            api=launch(Ingestion__DispatcherEnabled='true',Ingestion__Port='1')
            key=str(uuid.uuid4()); payload={'nombre':'Asíncrono','ubicacion':'Local','severidad':2,'descripcion':'Prueba','latitud':0,'longitud':0}
            body={'operations':[{'clientRequestId':key,'disaster':payload}]}
            def submit(value=body):return request(api,'/api/desastres/batch','POST',value,{'Prefer':'respond-async'})
            assert submit()[0]==202 and submit()[0]==202
            assert request(api)[2]==[]
            launch(Ingestion__DispatcherEnabled='true',Ingestion__ConsumersEnabled='true',Ingestion__Consumers='2')
            def final(value=body):
                for _ in range(150):
                    code,_,result=submit(value)
                    if code==200:return result['results'][0]
                    time.sleep(.1)
                raise AssertionError('No final result')
            receipt=final();assert receipt['status']=='Created';assert final()==receipt
            for _ in range(100):
                if len(broker('consumers/'+vhost))==2:break
                time.sleep(.1)
            else:raise AssertionError('Two competing consumers did not register')
            assert len(request(api)[2])==1
            conflict={'operations':[{'clientRequestId':key,'disaster':{**payload,'nombre':'Incompatible'}}]}
            assert final(conflict)['status']=='Conflict'
            id=receipt['disaster']['id']
            assert request(api,f'/api/desastres/{id}/fase','PUT',{'phase':'Response','expectedVersion':1,'reason':'Respuesta autorizada para el escenario'})[0]==200
            for _ in range(100):
                with closing(psycopg2.connect(database)) as conn,conn.cursor() as cursor:
                    cursor.execute('SELECT count(*) FROM disasters."DisasterOutboxMessages" WHERE "PublishedAt" IS NOT NULL')
                    if cursor.fetchone()[0]==1:break
                time.sleep(.1)
            else:raise AssertionError('Outbox not confirmed')
            assert request(api,'/api/desastres/sync')[2]['items'][0]['version']==2
            gap=str(uuid.uuid4())
            with closing(psycopg2.connect(database)) as conn,conn.cursor() as cursor:
                cursor.execute('ALTER TABLE disasters."DisasterCommandReceipts" ADD CONSTRAINT "LoseAck" CHECK ("ClientRequestId" <> %s OR "ResultJson" IS NULL)', (gap,));conn.commit()
            gap_body={'operations':[{'clientRequestId':gap,'disaster':{**payload,'nombre':'Gap'}}]}
            assert submit(gap_body)[0]==202
            for _ in range(100):
                with closing(psycopg2.connect(database)) as conn,conn.cursor() as cursor:
                    cursor.execute('SELECT count(*) FROM disasters."Desastres" WHERE "ClientRequestId"=%s',(gap,))
                    if cursor.fetchone()[0]==1:break
                time.sleep(.05)
            else:raise AssertionError('Creation did not persist before failed confirmation')
            worker=processes[-1];worker.kill();worker.wait()
            with closing(psycopg2.connect(database)) as conn,conn.cursor() as cursor:
                cursor.execute('ALTER TABLE disasters."DisasterCommandReceipts" DROP CONSTRAINT "LoseAck"');conn.commit()
            launch(Ingestion__DispatcherEnabled='true',Ingestion__ConsumersEnabled='true',Ingestion__Consumers='2')
            assert final(gap_body)['status']=='AlreadyProcessed'
            failed=str(uuid.uuid4());failed_body={'operations':[{'clientRequestId':failed,'disaster':{**payload,'nombre':'DLQ'}}]}
            with closing(psycopg2.connect(database)) as conn,conn.cursor() as cursor:
                cursor.execute("ALTER TABLE disasters.\"Desastres\" ADD CONSTRAINT \"TemporaryFailure\" CHECK (\"Nombre\" <> 'DLQ')");conn.commit()
            assert final(failed_body)['status']=='RetryableFailure'
            dead=[]
            for _ in range(100):
                dead=broker('queues/'+vhost+'/disasters.create.dlq/get','POST',{'count':1,'ackmode':'ack_requeue_true','encoding':'auto'})
                if dead:break
                time.sleep(.1)
            assert dead
            with closing(psycopg2.connect(database)) as conn,conn.cursor() as cursor:
                cursor.execute('ALTER TABLE disasters."Desastres" DROP CONSTRAINT "TemporaryFailure"');conn.commit()
            assert broker('exchanges/'+vhost+'/disasters.commands/publish','POST',{'properties':{'delivery_mode':2},'routing_key':'create','payload':dead[0]['payload'],'payload_encoding':dead[0]['payload_encoding']})['routed']
            for _ in range(100):
                if final(failed_body)['status']=='Created':break
                time.sleep(.1)
            assert final(failed_body)['status']=='Created'
            print('PASS: durable acceptance without broker, two consumers, replay, ACK loss, bounded retries, DLQ recovery, Outbox and Delta')
    finally:
        if 'log' in locals() and sys.exc_info()[0] is not None:
            log.flush(); log.seek(0); print(log.read()[-8000:])
        for process in reversed(processes):
            process.terminate()
            try:process.wait(timeout=5)
            except subprocess.TimeoutExpired:process.kill();process.wait()
        broker('vhosts/'+vhost,'DELETE')
        if 'log' in locals():log.close()

if __name__=='__main__':main()
