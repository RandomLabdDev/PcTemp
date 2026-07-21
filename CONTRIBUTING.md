# Contribuir a PcTemp

Gracias por colaborar. Antes de proponer cambios:

1. Abre una incidencia explicando el problema o la mejora.
2. Mantén los cambios centrados y evita incluir binarios de compilación.
3. Compila con `powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Clean`.
4. Comprueba los temas claro y oscuro, el escalado de Windows y una ventana con varias filas de tarjetas.
5. No incluyas diagnósticos reales sin eliminar números de serie, nombres de usuario y rutas privadas.

Las modificaciones relacionadas con sensores deben tolerar valores ausentes o anómalos. PcTemp debe mostrar `No disponible` cuando el hardware no exponga un dato, sin estimarlo ni bloquear la interfaz.

