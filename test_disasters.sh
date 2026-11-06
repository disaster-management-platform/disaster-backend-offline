#!/usr/bin/env bash

# Configuración de la URL del Gateway (ajusta si usas otro puerto u host)
BASE_URL="${API_BASE_URL:-http://localhost:5080/api/desastres}"

echo "============================================================"
echo " Registrando 20 desastres de prueba en Colombia"
echo " URL objetivo: ${BASE_URL}"
echo "============================================================"

# Función auxiliar para generar un UUID v4 sintético si no existe 'uuidgen'
generate_uuid() {
  if command -v uuidgen &>/dev/null; then
    uuidgen | tr '[:upper:]' '[:lower:]'
  else
    # Alternativa nativa en Linux/Bash mediante /dev/urandom
    cat /dev/urandom | tr -dc 'a-f0-9' | fold -w 32 | head -n 1 | sed -E 's/(.{8})(.{4})(.{4})(.{4})(.{12})/\1-\2-\3-\4-\5/'
  fi
}

# Arreglo de 20 desastres con datos geoespaciales y descripciones realistas
DISASTERS=(
  '{"nombre":"Desbordamiento del río Bogotá","ubicacion":"Soacha, Cundinamarca","severidad":4,"descripcion":"Inundación severa en barrios periféricos por lluvias intensas en la cuenca media.","latitud":4.5781,"longitud":-74.2144}'
  '{"nombre":"Deslizamiento en la Vía al Llano","ubicacion":"Guayabetal, Cundinamarca","severidad":5,"descripcion":"Masa de tierra bloquea totalmente el kilómetro 58. Cierre total de vía nacional y riesgo de colapso de viviendas adyacentes.","latitud":4.2142,"longitud":-73.8123}'
  '{"nombre":"Incendio forestal en Cerro de Tres Cruces","ubicacion":"Cali, Valle del Cauca","severidad":3,"descripcion":"Fuego de cobertura vegetal extendido por fuertes vientos. Varias cuadrillas de bomberos en la zona.","latitud":3.4589,"longitud":-76.5412}'
  '{"nombre":"Avalancha y crecida del río Mocoa","ubicacion":"Mocoa, Putumayo","severidad":5,"descripcion":"Crecida súbita de los ríos Mocoa, Sangoyaco y Mulato afectando múltiples barrios del casco urbano.","latitud":1.1528,"longitud":-76.6521}'
  '{"nombre":"Inundación por desbordamiento del río Atrato","ubicacion":"Quibdó, Chocó","severidad":4,"descripcion":"Inundación de zonas bajas afectando viviendas palafíticas y vías principales de comunicación fluvial.","latitud":5.6947,"longitud":-76.6611}'
  '{"nombre":"Colapso estructural por movimiento sísmico","ubicacion":"Armenia, Quindío","severidad":4,"descripcion":"Sismo superficial causa grietas estructurales graves en edificio residencial de 5 pisos. Evacuación preventiva en curso.","latitud":4.5339,"longitud":-75.6811}'
  '{"nombre":"Deslizamiento de tierra en Comuna 13","ubicacion":"Medellín, Antioquia","severidad":3,"descripcion":"Desprendimiento de ladera tras 48 horas de lluvia continua. Dos viviendas afectadas y amenaza a peatones.","latitud":6.2518,"longitud":-75.6152}'
  '{"nombre":"Vendaval e inundación costera","ubicacion":"Puerto Colombia, Atlántico","severidad":2,"descripcion":"Vientos huracanados causan destechamiento de locales comerciales en el malecón y marejada en la franja costera.","latitud":11.0219,"longitud":-74.9547}'
  '{"nombre":"Represamiento de la quebrada La Liboriana","ubicacion":"Salgar, Antioquia","severidad":4,"descripcion":"Acumulación de sedimento y vegetación genera alerta roja por posible avenida torrencial aguas abajo.","latitud":5.9644,"longitud":-75.9681}'
  '{"nombre":"Erupción de ceniza del Volcán Nevado del Ruiz","ubicacion":"Manizales, Caldas","severidad":3,"descripcion":"Caída constante de ceniza volcánica sobre el área metropolitana. Restricciones en operaciones aéreas y uso obligatorio de tapabocas.","latitud":4.8925,"longitud":-75.3188}'
  '{"nombre":"Inundación por ruptura del jarillón","ubicacion":"Cali, Valle del Cauca","severidad":4,"descripcion":"Filtración crítica en el Jarillón del río Cauca a la altura de Decepaz. Maquinaria pesada trabajando en contención.","latitud":3.4211,"longitud":-76.4822}'
  '{"nombre":"Incendio estructural en zona comercial","ubicacion":"Bucaramanga, Santander","severidad":2,"descripcion":"Conflagración en bodegas del centro de la ciudad. Sin víctimas fatales reportadas, pero con pérdiadas materiales altas.","latitud":7.1193,"longitud":-73.1227}'
  '{"nombre":"Granizada extrema y colapso de cubiertas","ubicacion":"Bogotá, D.C. (Chapinero)","severidad":2,"descripcion":"Fuerte tormenta con granizo bloquea colectores de agua pluvial provocando encharcamientos severos en la Cra 7.","latitud":4.6486,"longitud":-74.0628}'
  '{"nombre":"Pérdida de banca en vía panamericana","ubicacion":"Rosas, Cauca","severidad":5,"descripcion":"Macrodeslizamiento destruye tramo de la carretera Panamericana, dejando incomunicado el sur del país.","latitud":2.1642,"longitud":-76.7419}'
  '{"nombre":"Inundación en el sector de la Mojana","ubicacion":"Magangué, Bolívar","severidad":4,"descripcion":"Ruptura del punto Cara e Gato causa inundación de miles de hectáreas agrícolas y viviendas rurales.","latitud":9.2423,"longitud":-74.7544}'
  '{"nombre":"Hundimiento de vía urbana por alcantarillado","ubicacion":"Pasto, Nariño","severidad":1,"descripcion":"Falla en tubería principal genera socavón de 3 metros de diámetro. Tráfico vehicular desviado.","latitud":1.2136,"longitud":-77.2811}'
  '{"nombre":"Mar de leva y erosión costera","ubicacion":"Cartagena, Bolívar","severidad":2,"descripcion":"Oleaje anómalo genera inundaciones en la avenida Santander del barrio Bocagrande.","latitud":10.4042,"longitud":-75.5561}'
  '{"nombre":"Desbordamiento de la quebrada La Vieja","ubicacion":"Bogotá, D.C. (Usaquén)","severidad":2,"descripcion":"Escurrimiento masivo de agua y lodo sobre la carrera Séptima afectando el tránsito y locales del sector.","latitud":4.6983,"longitud":-74.0306}'
  '{"nombre":"Incendio forestal en el Parque Tayrona","ubicacion":"Santa Marta, Magdalena","severidad":3,"descripcion":"Fuego activo en sector boscoso de difícil acceso terrestre. Se requiere apoyo de helibalde para sofocar las llamas.","latitud":11.3012,"longitud":-74.0531}'
  '{"nombre":"Evolución de grieta por falla geológica","ubicacion":"San Francisco, Cundinamarca","severidad":3,"descripcion":"Desplazamiento lento de terreno afecta 15 predios rurales y la estructura de la escuela veredal.","latitud":4.9781,"longitud":-74.2922}'
)

COUNT=0
SUCCESS=0

for BODY in "${DISASTERS[@]}"; do
  COUNT=$((COUNT + 1))
  KEY=$(generate_uuid)

  echo -n "[$COUNT/20] Registrando desastre con Idempotency-Key: $KEY ... "

  # Realiza la petición POST enviando la cabecera de idempotencia
  RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "$BASE_URL" \
    -H "Content-Type: application/json" \
    -H "Idempotency-Key: $KEY" \
    -d "$BODY")

  HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
  HTTP_BODY=$(echo "$RESPONSE" | sed '$d')

  if [[ "$HTTP_CODE" == "201" || "$HTTP_CODE" == "200" ]]; then
    echo "✅ [HTTP $HTTP_CODE]"
    SUCCESS=$((SUCCESS + 1))
  else
    echo "❌ [HTTP $HTTP_CODE]"
    echo "   Respuesta: $HTTP_BODY"
  fi
done

echo "============================================================"
echo " Finalizado: $SUCCESS de $COUNT desastres registrados correctamente."
echo "============================================================"
