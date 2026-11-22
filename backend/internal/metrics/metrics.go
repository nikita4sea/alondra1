// Package metrics is a small Prometheus-compatible registry (text exposition format 0.0.4) without dependencies.
// Labels are fixed, low-cardinality values (route patterns, result names); never ids, emails, or tokens.
package metrics

import (
	"fmt"
	"io"
	"math"
	"net/http"
	"sort"
	"strings"
	"sync"
	"time"
)

type Registry struct {
	mu       sync.Mutex
	counters map[string]*family
	gauges   map[string]*family
	hists    map[string]*histogram
	help     map[string]string
	// Collectors run on every scrape (for values read from elsewhere, such as database health).
	collectors []func(*Registry)
}

type family struct {
	values map[string]float64 // key: rendered label set
}

type histogram struct {
	buckets []float64
	series  map[string]*histSeries
}

type histSeries struct {
	counts []uint64
	sum    float64
	count  uint64
}

func New() *Registry {
	return &Registry{counters: map[string]*family{}, gauges: map[string]*family{}, hists: map[string]*histogram{}, help: map[string]string{}}
}

// Labels renders label pairs ("route", "/v1/me", "status", "2xx") in a stable order.
func Labels(pairs ...string) string {
	if len(pairs) == 0 {
		return ""
	}
	parts := make([]string, 0, len(pairs)/2)
	for i := 0; i+1 < len(pairs); i += 2 {
		value := strings.NewReplacer(`\`, `\\`, `"`, `\"`, "\n", `\n`).Replace(pairs[i+1])
		parts = append(parts, fmt.Sprintf(`%s="%s"`, pairs[i], value))
	}
	sort.Strings(parts)
	return "{" + strings.Join(parts, ",") + "}"
}

func (r *Registry) Help(name, text string) {
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.help[name] = text
}

// Add increases a counter. A nil registry ignores calls, so instrumented code works without metrics.
func (r *Registry) Add(name string, delta float64, labels ...string) {
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	f := r.counters[name]
	if f == nil {
		f = &family{values: map[string]float64{}}
		r.counters[name] = f
	}
	f.values[Labels(labels...)] += delta
}

func (r *Registry) Inc(name string, labels ...string) { r.Add(name, 1, labels...) }

func (r *Registry) Set(name string, value float64, labels ...string) {
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	f := r.gauges[name]
	if f == nil {
		f = &family{values: map[string]float64{}}
		r.gauges[name] = f
	}
	f.values[Labels(labels...)] = value
}

// Observe records a value in a histogram with the given upper bounds (seconds for durations).
func (r *Registry) Observe(name string, buckets []float64, value float64, labels ...string) {
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	h := r.hists[name]
	if h == nil {
		h = &histogram{buckets: buckets, series: map[string]*histSeries{}}
		r.hists[name] = h
	}
	key := Labels(labels...)
	s := h.series[key]
	if s == nil {
		s = &histSeries{counts: make([]uint64, len(h.buckets))}
		h.series[key] = s
	}
	for i, upper := range h.buckets {
		if value <= upper {
			s.counts[i]++
		}
	}
	s.sum += value
	s.count++
}

// OnScrape registers a function that refreshes gauges before each scrape.
func (r *Registry) OnScrape(fn func(*Registry)) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.collectors = append(r.collectors, fn)
}

func (r *Registry) Write(w io.Writer) {
	r.mu.Lock()
	collectors := append([]func(*Registry){}, r.collectors...)
	r.mu.Unlock()
	for _, collect := range collectors {
		collect(r)
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	writeFamilies := func(kind string, families map[string]*family) {
		names := sortedKeys(families)
		for _, name := range names {
			if help, ok := r.help[name]; ok {
				fmt.Fprintf(w, "# HELP %s %s\n", name, help)
			}
			fmt.Fprintf(w, "# TYPE %s %s\n", name, kind)
			for _, labels := range sortedKeys(families[name].values) {
				fmt.Fprintf(w, "%s%s %s\n", name, labels, format(families[name].values[labels]))
			}
		}
	}
	writeFamilies("counter", r.counters)
	writeFamilies("gauge", r.gauges)
	for _, name := range sortedKeys(r.hists) {
		h := r.hists[name]
		if help, ok := r.help[name]; ok {
			fmt.Fprintf(w, "# HELP %s %s\n", name, help)
		}
		fmt.Fprintf(w, "# TYPE %s histogram\n", name)
		for _, labels := range sortedKeys(h.series) {
			s := h.series[labels]
			inner := strings.TrimSuffix(strings.TrimPrefix(labels, "{"), "}")
			join := func(le string) string {
				if inner == "" {
					return `{le="` + le + `"}`
				}
				return "{" + inner + `,le="` + le + `"}`
			}
			for i, upper := range h.buckets {
				fmt.Fprintf(w, "%s_bucket%s %d\n", name, join(format(upper)), s.counts[i])
			}
			fmt.Fprintf(w, "%s_bucket%s %d\n", name, join("+Inf"), s.count)
			fmt.Fprintf(w, "%s_sum%s %s\n", name, labels, format(s.sum))
			fmt.Fprintf(w, "%s_count%s %d\n", name, labels, s.count)
		}
	}
}

func (r *Registry) Handler() http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/plain; version=0.0.4; charset=utf-8")
		r.Write(w)
	})
}

// DurationBuckets suit HTTP requests (seconds).
var DurationBuckets = []float64{0.005, 0.025, 0.1, 0.25, 0.5, 1, 2.5, 10}

func Since(start time.Time) float64 { return time.Since(start).Seconds() }

func format(v float64) string {
	if math.IsInf(v, 1) {
		return "+Inf"
	}
	if v == math.Trunc(v) && math.Abs(v) < 1e15 {
		return fmt.Sprintf("%d", int64(v))
	}
	return fmt.Sprintf("%g", v)
}

func sortedKeys[V any](m map[string]V) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}
