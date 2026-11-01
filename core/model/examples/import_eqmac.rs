//! Print eqMac presets as MyOwnEQ profiles and render their response.
//! Usage: cargo run -p myowneq-model --example import_eqmac [path/to/com.bitgapp.eqmac.plist]

use myowneq_dsp::response::{log_grid, response_db};
use myowneq_model::formats::eqmac;
use myowneq_model::RenderContext;

fn main() {
    let path = std::env::args()
        .nth(1)
        .map(Into::into)
        .or_else(eqmac::default_plist_path)
        .expect("no path");
    let profiles = eqmac::import_plist(&path).expect("import failed");
    let freqs = log_grid(20.0, 20_000.0, 10);
    for p in profiles {
        let params = p.render(&RenderContext::default());
        let curve = response_db(params.active_bands(), params.preamp_db, 48_000.0, &freqs);
        println!(
            "{:<34} preamp {:>6.2} dB  bands {:>2}  curve: {}",
            p.name,
            params.preamp_db,
            params.band_count,
            curve
                .iter()
                .map(|d| format!("{d:+.1}"))
                .collect::<Vec<_>>()
                .join(" ")
        );
    }
}
