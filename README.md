# OpenDriverUpdater

Herramienta **de código abierto** para Windows 10/11 que mantiene tus drivers al día de forma sencilla:

1. **Al abrirla**, analiza los componentes de tu PC y los drivers que tienen instalados.
2. **Comprueba** si existe una versión más reciente publicada por el fabricante.
3. **Tú eliges** qué drivers actualizar marcando las casillas.
4. **Con un botón** los descarga, los instala y te pregunta si quieres reiniciar.

Antes de instalar crea (opcionalmente) un **punto de restauración**, para poder volver atrás si algún driver da problemas.

## ¿De dónde salen los drivers?

Cada dispositivo se compara con **tres fuentes oficiales**:

| Fuente | Qué aporta |
|---|---|
| **Windows Update** | Los drivers que Microsoft ofrece automáticamente a tu PC (incluidos los opcionales). |
| **Catálogo de Microsoft Update** (`catalog.update.microsoft.com`) | Todas las versiones que cada fabricante (Intel, AMD, NVIDIA, Realtek, Qualcomm...) ha publicado para el *Hardware ID* exacto de tu dispositivo, aunque Windows Update no te las ofrezca. |
| **Web oficial de NVIDIA** | La última versión publicada por NVIDIA para tu tarjeta gráfica, que suele salir antes que en Windows Update. |

La lista muestra **todos** los dispositivos con:

- versión y fecha del driver **instalado**,
- versión y fecha de la **última versión oficial** encontrada y su fuente,
- el **estado**: actualización disponible, sin driver, driver del fabricante disponible (tienes el genérico de Microsoft), al día, o sin datos.

Solo se marcan por defecto las actualizaciones claras (mismo fabricante y coincidencia exacta con tu modelo). Las coincidencias **genéricas** (encontradas por un ID menos específico) se muestran, pero tú decides si instalarlas.

Doble clic en una fila muestra los detalles (todas las versiones encontradas, Hardware IDs, enlace oficial). **Exportar informe** guarda un .txt en el escritorio con todo el análisis, útil para reportar problemas.

### Cómo se instala cada fuente

- **Windows Update**: con el propio motor de Windows Update.
- **Catálogo**: se descarga el paquete `.cab` oficial, se extrae y se instala con `pnputil /add-driver /install`, la herramienta de Windows.
- **NVIDIA**: se descarga el instalador oficial y se abre el asistente de NVIDIA.

### Limitaciones conocidas

- AMD (gráficas Radeon) e Intel no tienen un servicio público para consultar su última versión; para ellos se usa el Catálogo de Microsoft, donde publican con algo de retraso.
- Los drivers de portátiles adaptados por el fabricante del equipo (Dell, HP, Lenovo...) pueden no coincidir con la versión genérica del fabricante del chip.

## Requisitos

- Windows 10 u 11 (x64 o ARM64)
- Permisos de administrador (el programa los pide al abrirse)
- Conexión a Internet y el servicio «Windows Update» sin deshabilitar

## Compilar

Necesitas el [SDK de .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# Ejecutar en modo desarrollo
dotnet run --project src/OpenDriverUpdater

# Generar un único .exe autónomo (no requiere tener .NET instalado)
dotnet publish src/OpenDriverUpdater -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

El ejecutable queda en `publish/OpenDriverUpdater.exe`.

Si subes el proyecto a GitHub, el flujo de `.github/workflows/build.yml` compila el `.exe` automáticamente en cada *push* y lo deja descargable en la pestaña **Actions**.

## Estructura

```
src/OpenDriverUpdater/
├── Program.cs                      Punto de entrada
├── MainForm.cs                     Interfaz (lista con casillas, botones, progreso)
├── app.manifest                    Pide permisos de administrador
├── Models/Models.cs                Driver instalado, actualización, resultado
└── Services/
    ├── DeviceScanner.cs            Dispositivos, drivers instalados y Hardware IDs (WMI)
    ├── UpdateEngine.cs             Consulta las fuentes y compara versiones
    ├── WindowsUpdateService.cs     Windows Update Agent
    ├── CatalogService.cs           Catálogo de Microsoft Update (búsqueda, descarga, pnputil)
    ├── NvidiaService.cs            Última versión en la web de NVIDIA
    ├── VersionUtil.cs              Comparación de versiones
    ├── Http.cs                     Descargas y reintentos
    └── SystemActions.cs            Punto de restauración y reinicio
```

## Hoja de ruta (ideas para contribuir)

- [x] Catálogo de Microsoft Update y web de NVIDIA como fuentes adicionales
- [ ] Fuentes directas de AMD e Intel
- [ ] Copia de seguridad de los drivers actuales (`pnputil /export-driver`) antes de actualizar
- [ ] Ocultar un driver para que no se vuelva a ofrecer
- [ ] Botón de cancelar durante la descarga
- [ ] Traducciones (inglés, catalán, etc.)
- [ ] Modo línea de comandos para automatizar (`--scan`, `--update-all`)

## Licencia

MIT — ver [LICENSE](LICENSE).
