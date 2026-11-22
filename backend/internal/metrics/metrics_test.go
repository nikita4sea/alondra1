package metrics

import (
	"strings"
	"testing"
)

func TestExpositionFormat(t *testing.T) {
	r := New()
	r.Help("alondra1_http_requests_total", "HTTP requests.")
	r.Inc("alondra1_http_requests_total", "route", "GET /v1/me", "status", "2xx")
	r.Inc("alondra1_http_requests_total", "status", "2xx", "route", "GET /v1/me") // label order does not matter
	r.Set("alondra1_db_up", 1)
	r.Observe("alondra1_http_request_duration_seconds", []float64{0.1, 1}, 0.05, "route", "GET /v1/me")
	r.Observe("alondra1_http_request_duration_seconds", []float64{0.1, 1}, 0.5, "route", "GET /v1/me")
	r.OnScrape(func(r *Registry) { r.Set("alondra1_scrapes", 7) })
	var out strings.Builder
	r.Write(&out)
	text := out.String()
	for _, want := range []string{
		"# HELP alondra1_http_requests_total HTTP requests.\n",
		"# TYPE alondra1_http_requests_total counter\n",
		`alondra1_http_requests_total{route="GET /v1/me",status="2xx"} 2` + "\n",
		"# TYPE alondra1_db_up gauge\nalondra1_db_up 1\n",
		`alondra1_http_request_duration_seconds_bucket{route="GET /v1/me",le="0.1"} 1` + "\n",
		`alondra1_http_request_duration_seconds_bucket{route="GET /v1/me",le="1"} 2` + "\n",
		`alondra1_http_request_duration_seconds_bucket{route="GET /v1/me",le="+Inf"} 2` + "\n",
		`alondra1_http_request_duration_seconds_count{route="GET /v1/me"} 2` + "\n",
		"alondra1_scrapes 7\n",
	} {
		if !strings.Contains(text, want) {
			t.Errorf("missing %q in:\n%s", want, text)
		}
	}
}

func TestLabelValuesAreEscapedAndNilRegistryIsSafe(t *testing.T) {
	if got := Labels("route", `a"b\c`); got != `{route="a\"b\\c"}` {
		t.Fatal(got)
	}
	var r *Registry
	r.Inc("x")
	r.Set("y", 1)
	r.Observe("z", DurationBuckets, 1)
}
