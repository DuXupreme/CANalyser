// Presentation only: persisted event names and NDJSON remain technical.
export const EVENT_CATALOG = {
  app_started: { label: "App gestart", category: "Systeem" },
  app_crashed: { label: "App gecrasht", category: "Systeem", outcome: "error" },
  app_unexpected_exit: { label: "App onverwacht afgesloten", category: "Systeem", outcome: "error" },
  previous_operation_interrupted: { label: "Vorige bewerking onderbroken", category: "Systeem", outcome: "error" },
  load_decode_completed: { label: "Log geladen en gedecodeerd", category: "Logs laden", usage: true },
  load_decode_failed: { label: "Log laden mislukt", category: "Logs laden", outcome: "error" },
  load_decode_cancelled: { label: "Log laden geannuleerd", category: "Logs laden", outcome: "cancelled" },
  export_decoded_csv: { label: "Gedecodeerde data geëxporteerd naar CSV", category: "Export", usage: true },
  settings_applied: { label: "Instellingen opgeslagen", category: "Instellingen", usage: true },
  analysis_layout_exported: { label: "Grafiekindeling opgeslagen", category: "Grafiekindelingen", usage: true },
  analysis_layout_imported: { label: "Grafiekindeling ingeladen", category: "Grafiekindelingen", usage: true },
  analysis_apply_plot_groups: { label: "Grafiekgroepen toegepast", category: "Grafieken", usage: true },
  analysis_open_detached_plots: { label: "Apart grafiekvenster geopend", category: "Grafieken", usage: true },
  analysis_lod_forced: { label: "Grafiekdetailniveau geforceerd", category: "Grafieken", usage: true },
  actuator_csv_comparison_loaded: { label: "Actuatorvergelijking ingeladen", category: "Actuatoranalyse", usage: true },
  update_check_skipped: { label: "Updatecontrole overgeslagen", category: "Updates" },
  update_check_completed: { label: "Updatecontrole uitgevoerd", category: "Updates" },
  update_prompt_declined: { label: "Update uitgesteld", category: "Updates" },
  update_apply_failed: { label: "Update installeren mislukt", category: "Updates", outcome: "error" },
  analytics_recompute_started: { label: "Analyse gestart", category: "Analyse" },
  analytics_recompute_completed: { label: "Analyse voltooid", category: "Analyse", usage: true },
  analytics_recompute_failed: { label: "Analyse mislukt", category: "Analyse", outcome: "error" },
  busmaster_filter_started: { label: "BUSMASTER-filter gestart", category: "BUSMASTER" },
  busmaster_filter_completed: { label: "BUSMASTER-filter voltooid", category: "BUSMASTER", usage: true },
  busmaster_filter_failed: { label: "BUSMASTER-filter mislukt", category: "BUSMASTER", outcome: "error" },
  dispatcher_unhandled_exception: { label: "UI-crash onderschept", category: "Stabiliteit", outcome: "error" },
  app_unhandled_exception: { label: "App-crash onderschept", category: "Stabiliteit", outcome: "error" },
  unobserved_task_exception: { label: "Onbehandelde achtergrondfout", category: "Stabiliteit", outcome: "error" }
};

export const USAGE_EVENTS = Object.keys(EVENT_CATALOG).filter(name => EVENT_CATALOG[name].usage);
