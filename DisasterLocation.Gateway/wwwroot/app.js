/* global L */
const form = document.querySelector('#disaster-form');
const submitButton = document.querySelector('#submit');
const refreshButton = document.querySelector('#refresh');
const formStatus = document.querySelector('#form-status');
const listStatus = document.querySelector('#list-status');
const latitude = document.querySelector('#latitud');
const longitude = document.querySelector('#longitud');
const colors = ['#27815b', '#738520', '#cb9223', '#d16b30', '#b83d46'];
let map;
let markers;
let selection;

function setStatus(element, message, isError = false) {
  element.textContent = message;
  element.dataset.error = String(isError);
}

if (typeof L !== 'undefined') {
  map = L.map('map').setView([4.5709, -74.2973], 5);
  const tiles = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
  }).addTo(map);
  tiles.on('tileerror', () => {
    document.querySelector('#map-hint').textContent = 'No se pudo cargar el mapa base. Revisa tu conexión a internet. Puedes ingresar las coordenadas manualmente.';
  });
  markers = L.featureGroup().addTo(map);
  map.on('click', ({ latlng }) => {
    const point = latlng.wrap();
    latitude.value = point.lat.toFixed(6);
    longitude.value = point.lng.toFixed(6);
    updateSelection();
  });
} else {
  document.querySelector('#map-hint').textContent = 'El mapa no está disponible. Puedes escribir las coordenadas y consultar los reportes en la lista.';
}

function updateSelection() {
  if (!map) return;
  if (selection) { selection.remove(); selection = undefined; }
  if (!latitude.value || !longitude.value || !latitude.validity.valid || !longitude.validity.valid) return;
  selection = L.circleMarker([Number(latitude.value), Number(longitude.value)], {
    radius: 10, color: '#193c36', fillColor: '#fff', fillOpacity: .9, weight: 3, dashArray: '4 3'
  }).addTo(map).bindTooltip('Ubicación del nuevo reporte');
}

latitude.addEventListener('input', updateSelection);
longitude.addEventListener('input', updateSelection);

async function api(path, options) {
  const response = await fetch(`/api/desastres${path}`, options);
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    const messages = problem?.errors ? Object.values(problem.errors).flat().join(' ') : null;
    throw new Error(messages || `No se pudo completar la solicitud (HTTP ${response.status}). Comprueba que la API esté ejecutándose.`);
  }
  return response.json();
}

function textElement(tag, text, className) {
  const element = document.createElement(tag);
  element.textContent = text;
  if (className) element.className = className;
  return element;
}

function render(disasters, fitMap) {
  document.querySelector('#count').textContent = String(disasters.length);
  const list = document.querySelector('#disaster-list');
  list.replaceChildren();
  if (markers) markers.clearLayers();
  if (disasters.length === 0) {
    list.append(textElement('p', 'Todavía no hay reportes. Registra el primero con el formulario.', 'empty'));
    return;
  }
  for (const disaster of disasters) {
    let marker;
    if (map) {
      const popup = document.createElement('div');
      popup.append(textElement('h3', disaster.nombre, 'popup-title'),
        textElement('p', disaster.ubicacion),
        textElement('p', `Severidad: ${disaster.severidad}/5`),
        textElement('p', disaster.descripcion, 'popup-text'));
      marker = L.circleMarker([disaster.latitud, disaster.longitud], {
        radius: 7 + disaster.severidad, color: '#fff', weight: 2,
        fillColor: colors[disaster.severidad - 1], fillOpacity: .9
      }).bindPopup(popup).addTo(markers);
    }
    const article = document.createElement('article');
    article.className = 'report';
    const content = document.createElement('div');
    content.append(textElement('h3', disaster.nombre), textElement('p', disaster.ubicacion),
      textElement('p', disaster.descripcion),
      textElement('p', `${disaster.latitud.toFixed(6)}, ${disaster.longitud.toFixed(6)}`));
    if (map) {
      const locate = textElement('button', 'Ver en el mapa ↗', 'location-button');
      locate.type = 'button';
      locate.setAttribute('aria-label', `Ver ${disaster.nombre} en el mapa`);
      locate.addEventListener('click', () => {
        map.setView(marker.getLatLng(), 12);
        marker.openPopup();
        document.querySelector('#map').scrollIntoView({ behavior: 'smooth', block: 'center' });
      });
      content.append(locate);
    }
    const severity = textElement('span', `Nivel ${disaster.severidad}`, `severity level-${disaster.severidad}`);
    article.append(content, severity);
    list.append(article);
  }
  if (map && fitMap) map.fitBounds(markers.getBounds(), { padding: [35, 35], maxZoom: 12 });
}

async function loadDisasters(fitMap = true) {
  refreshButton.disabled = true;
  setStatus(listStatus, 'Cargando reportes…');
  try {
    render(await api(''), fitMap);
    setStatus(listStatus, '');
  } catch (error) {
    setStatus(listStatus, error.message, true);
  } finally {
    refreshButton.disabled = false;
  }
}

form.addEventListener('submit', async event => {
  event.preventDefault();
  if (!form.reportValidity()) return;
  const data = new FormData(form);
  const disaster = {
    nombre: data.get('nombre'), ubicacion: data.get('ubicacion'),
    severidad: Number(data.get('severidad')), descripcion: data.get('descripcion'),
    latitud: Number(data.get('latitud')), longitud: Number(data.get('longitud'))
  };
  submitButton.disabled = true;
  setStatus(formStatus, 'Guardando reporte…');
  try {
    const saved = await api('', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(disaster)
    });
    form.reset();
    updateSelection();
    setStatus(formStatus, `Reporte #${saved.id} registrado correctamente.`);
    await loadDisasters();
  } catch (error) {
    setStatus(formStatus, error.message, true);
  } finally {
    submitButton.disabled = false;
  }
});

refreshButton.addEventListener('click', () => loadDisasters());
loadDisasters();
