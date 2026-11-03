# OpenDriverUpdater

Un actualizador de drivers para Windows 10/11, gratis y de código abierto. Lo hice porque los que hay suelen venir con publicidad, versiones de pago o drivers de sitios raros.

**[⬇ Descargar OpenDriverUpdater.exe](https://github.com/Alesito1313/OpenDriverUpdater/releases/latest/download/OpenDriverUpdater.exe)** (un solo archivo, no hay que instalar nada)

Qué hace:

1. Al abrirlo mira qué componentes tiene tu PC y qué driver tiene cada uno.
2. Busca la última versión oficial de cada driver.
3. Te enseña la versión instalada y la última al lado, y tú marcas cuáles quieres actualizar.
4. Le das a actualizar, descarga e instala, y te pregunta si quieres reiniciar.

Antes de instalar crea un punto de restauración por si algo sale mal (se puede quitar).

Idiomas: inglés, español, alemán, francés e italiano (la bandera de arriba a la derecha).

## De dónde saca los drivers

Solo de sitios oficiales:

| Fuente | Para qué |
|---|---|
| **Windows Update** | Lo que Microsoft ofrece para tu PC, incluidos los opcionales. |
| **Catálogo de Microsoft Update** | Todas las versiones que Intel, AMD, NVIDIA, Realtek, etc. han subido para el *Hardware ID* exacto de tu dispositivo, aunque Windows Update no te las ofrezca. |
| **Web de NVIDIA** | Si tienes gráfica NVIDIA, la última versión de su web, que suele salir antes. |

Si un driver solo coincide con un ID genérico (puede no ser para tu modelo exacto) se avisa y no se marca solo.

Cosas que de momento no hace: AMD (Radeon) e Intel no tienen una forma pública de consultar su última versión, así que para ellos se usa el catálogo de Microsoft, que va algo más atrasado. Y los drivers adaptados por el fabricante del portátil (Dell, HP, Lenovo…) pueden no coincidir con los genéricos.

## «Windows protegió su PC»

Sale con cualquier programa que no esté firmado digitalmente y que aún tenga pocas descargas. Para abrirlo: **Más información → Ejecutar de todas formas**.

Para quitarlo hace falta firmar el exe con un certificado de firma de código. La compilación ya está preparada: si se añaden los secretos `SIGN_CERT_BASE64` y `SIGN_CERT_PASSWORD` en el repositorio, cada versión sale firmada. Opciones: [SignPath Foundation](https://signpath.org/) (gratis para proyectos open source con algo de trayectoria), un certificado OV de pago (Certum, Sectigo…) o publicarlo en la Microsoft Store.

## Privacidad

No recoge datos ni tiene telemetría. Para buscar drivers solo envía los identificadores de hardware de tus dispositivos (tipo `PCI\VEN_10EC&DEV_8168`) a Microsoft y, si tienes gráfica NVIDIA, a NVIDIA. El informe que se puede exportar se guarda en tu escritorio y ya está.

## Requisitos

- Windows 10 u 11 de 64 bits
- .NET Framework 4.8 y WebView2 (los dos vienen ya con Windows 10/11 actualizado)
- Permisos de administrador (para instalar drivers hacen falta)

## Compilar

Con el [SDK de .NET](https://dotnet.microsoft.com/download) instalado:

```
dotnet build src/OpenDriverUpdater -c Release -o out
```

Sale `out/OpenDriverUpdater.exe`. Cada push a `main` lo compila GitHub Actions y lo publica en Releases.

## Cómo está hecho

```
src/OpenDriverUpdater/
├── Program.cs           arranque; carga las dll de WebView2 que van dentro del exe
├── MainForm.cs          la ventana: muestra la interfaz web y hace de puente con el motor
├── Lang.cs              textos del lado C# en los 5 idiomas
├── Native.cs            color de la barra de título
├── Core/
│   ├── DeviceScanner.cs dispositivos y drivers instalados (WMI)
│   ├── Engine.cs        pregunta a las 3 fuentes y compara versiones
│   ├── Catalog.cs       catálogo de Microsoft Update (buscar, descargar, instalar con pnputil)
│   ├── Nvidia.cs        última versión en la web de NVIDIA
│   ├── WindowsUpdate.cs Windows Update por COM
│   ├── Versions.cs      comparar versiones
│   ├── Net.cs           descargas y reintentos
│   └── SystemTools.cs   punto de restauración y reinicio
└── ui/                  la interfaz (HTML/CSS/JS): index.html, styles.css, app.js, spring.js, i18n.js
```

La interfaz usa animaciones con muelles (spring.js) para que todo responda al momento y se pueda interrumpir.

## Ideas pendientes

- [ ] Copia de los drivers actuales antes de actualizar (`pnputil /export-driver`)
- [ ] Ocultar un driver para que no vuelva a salir
- [ ] AMD e Intel directamente desde su web
- [ ] Modo consola (`--scan`, `--update-all`)

## Licencia

MIT
