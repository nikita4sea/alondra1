// Package client is a reference offline-first sync client for the Alondra1 cloud API. It is the executable
// specification of the client side of docs/BACKEND_SPEC.md §10 and drives the multi-device tests; the Windows,
// Android, and iOS adapters implement the same behavior in their own stores.
//
// Local writes never wait for the network: they change the local copy and enter an outbox. Sync pushes the outbox
// (idempotently, by mutation id), then pulls changes. Conflicts keep the local edit for the user to resolve.
package client

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"sort"

	"alondra1/backend/internal/tasks"
)

// Item is the local copy of one cloud resource.
type Item struct {
	Type    string          // group, list, task, completion, user_config, tenant_config
	ID      string
	Data    json.RawMessage // the todo-v1 object (or settings document)
	Version int64           // the server version this copy is based on (0 = never on the server)
	Deleted bool
}

// Conflict: the server refused a local edit because the record changed (or was deleted) elsewhere. Nothing was lost:
// Local is what this device wanted, Server is the current server copy (nil when deleted there).
type Conflict struct {
	Mutation tasks.Mutation
	Local    json.RawMessage
	Server   map[string]any
	Deleted  bool
}

type Client struct {
	BaseURL  string
	Token    string
	TenantID string
	HTTP     *http.Client

	Items     map[string]*Item
	Outbox    []tasks.Mutation
	Conflicts []Conflict
	Rejected  []tasks.MutationResult
	Orphaned  []tasks.Mutation // unsent when access to the space ended
	Cursor    string
	Purged    bool
	Online    bool // tests switch this off to simulate offline work
}

func New(baseURL, token, tenantID string) *Client {
	return &Client{BaseURL: baseURL, Token: token, TenantID: tenantID, HTTP: http.DefaultClient, Items: map[string]*Item{}, Online: true}
}

func key(resourceType, id string) string { return resourceType + "/" + id }

func (c *Client) Get(resourceType, id string) *Item { return c.Items[key(resourceType, id)] }

func newMutationID() string {
	b := make([]byte, 12)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// ---------- local writes (offline-safe) ----------

// Create adds a resource locally and queues it.
func (c *Client) Create(resourceType string, object map[string]any) {
	raw, _ := json.Marshal(object)
	id := object["id"].(string)
	c.Items[key(resourceType, id)] = &Item{Type: resourceType, ID: id, Data: raw}
	c.Outbox = append(c.Outbox, tasks.Mutation{MutationID: newMutationID(), ResourceType: resourceType, Action: "create", ResourceID: id, Resource: raw})
}

// Patch merges changes into the local copy and queues them. Several offline edits of one record become one pending
// mutation, all based on the last version this device saw.
func (c *Client) Patch(resourceType, id string, changes map[string]any, appendCompletion map[string]any) error {
	item := c.Items[key(resourceType, id)]
	if item == nil || item.Deleted {
		return fmt.Errorf("%s %s is not here", resourceType, id)
	}
	patch, _ := json.Marshal(changes)
	merged, err := tasks.MergePatch(item.Data, patch)
	if err != nil {
		return err
	}
	item.Data = merged
	var completion json.RawMessage
	if appendCompletion != nil {
		completion, _ = json.Marshal(appendCompletion)
	}
	for i := range c.Outbox {
		pending := &c.Outbox[i]
		if pending.ResourceType != resourceType || pending.ResourceID != id || completion != nil {
			continue
		}
		switch pending.Action {
		case "create":
			pending.Resource = merged
			return nil
		case "patch":
			if pending.AppendCompletion == nil {
				combined, err := tasks.ComposePatch(pending.Changes, patch) // not MergePatch: nulls must survive
				if err != nil {
					return err
				}
				pending.Changes = combined
				return nil
			}
		}
	}
	c.Outbox = append(c.Outbox, tasks.Mutation{MutationID: newMutationID(), ResourceType: resourceType, Action: "patch", ResourceID: id,
		BaseVersion: item.Version, Changes: patch, AppendCompletion: completion})
	return nil
}

// Delete removes a resource locally and queues the deletion (dropping a never-sent create).
func (c *Client) Delete(resourceType, id string) {
	item := c.Items[key(resourceType, id)]
	if item == nil {
		return
	}
	item.Deleted = true
	kept := c.Outbox[:0]
	neverSent := false
	for _, m := range c.Outbox {
		if m.ResourceType == resourceType && m.ResourceID == id {
			neverSent = neverSent || m.Action == "create"
			continue
		}
		kept = append(kept, m)
	}
	c.Outbox = kept
	if !neverSent {
		c.Outbox = append(c.Outbox, tasks.Mutation{MutationID: newMutationID(), ResourceType: resourceType, Action: "delete", ResourceID: id, BaseVersion: item.Version})
	}
}

// ResolveKeepServer drops the local edit of a conflict (the server copy is already applied locally).
func (c *Client) ResolveKeepServer(i int) { c.Conflicts = append(c.Conflicts[:i], c.Conflicts[i+1:]...) }

// ResolveReapply deliberately re-applies the local edit on top of the server version (a new mutation).
func (c *Client) ResolveReapply(i int) error {
	conflict := c.Conflicts[i]
	c.Conflicts = append(c.Conflicts[:i], c.Conflicts[i+1:]...)
	var changes map[string]any
	if conflict.Mutation.Action == "patch" {
		_ = json.Unmarshal(conflict.Mutation.Changes, &changes)
	} else {
		_ = json.Unmarshal(conflict.Local, &changes)
		delete(changes, "id")
	}
	return c.Patch(conflict.Mutation.ResourceType, conflict.Mutation.ResourceID, changes, nil)
}

// ---------- sync ----------

// ErrOffline is returned while Online is false.
var ErrOffline = errors.New("offline")

type apiError struct {
	Status int
	Code   string
}

func (e *apiError) Error() string { return fmt.Sprintf("HTTP %d %s", e.Status, e.Code) }

// Sync pushes the outbox, then pulls (bootstrapping when needed). It is safe to call repeatedly.
func (c *Client) Sync(ctx context.Context) error {
	if !c.Online {
		return ErrOffline
	}
	if err := c.push(ctx); err != nil {
		return c.handle(err)
	}
	return c.handle(c.pull(ctx))
}

// Pull only reads (for example on app resume before pushing); unsent edits stay in the outbox and stay visible.
func (c *Client) Pull(ctx context.Context) error {
	if !c.Online {
		return ErrOffline
	}
	return c.handle(c.pull(ctx))
}

func (c *Client) handle(err error) error {
	var api *apiError
	if errors.As(err, &api) && api.Status == http.StatusNotFound && api.Code == "TENANT_NOT_FOUND" {
		// Access to the space ended: remove the cloud copy; keep unsent edits aside so nothing is silently lost.
		c.Orphaned = append(c.Orphaned, c.Outbox...)
		c.Outbox, c.Items, c.Cursor, c.Purged = nil, map[string]*Item{}, "", true
		return nil
	}
	return err
}

func (c *Client) push(ctx context.Context) error {
	for len(c.Outbox) > 0 {
		batch := c.Outbox
		if len(batch) > tasks.MaxPushBatch {
			batch = batch[:tasks.MaxPushBatch]
		}
		var response struct {
			Results []tasks.MutationResult `json:"results"`
		}
		if err := c.call(ctx, "POST", "/v1/sync/push", nil, map[string]any{"mutations": batch}, &response); err != nil {
			return err // the outbox is untouched; the same batch is sent again later and answered from receipts
		}
		for i, result := range response.Results {
			c.settle(batch[i], result)
		}
		c.Outbox = c.Outbox[len(response.Results):]
	}
	return nil
}

func (c *Client) settle(m tasks.Mutation, result tasks.MutationResult) {
	k := key(m.ResourceType, m.ResourceID)
	switch result.Status {
	case "applied":
		if result.Record != nil {
			c.applyRecord(m.ResourceType, result.Record, true)
		} else if item := c.Items[k]; item != nil {
			item.Version = m.BaseVersion + 1 // a delete's tombstone version
		}
	case "conflict":
		local := json.RawMessage(nil)
		if item := c.Items[k]; item != nil {
			local = item.Data
		}
		c.Conflicts = append(c.Conflicts, Conflict{Mutation: m, Local: local, Server: result.Current, Deleted: result.CurrentDeleted})
		if result.Current != nil {
			c.applyRecord(m.ResourceType, result.Current, true)
			c.Items[k].Deleted = result.CurrentDeleted
		} else if result.CurrentDeleted {
			if item := c.Items[k]; item != nil {
				item.Deleted = true
			}
		}
	default:
		c.Rejected = append(c.Rejected, result)
	}
}

func (c *Client) pull(ctx context.Context) error {
	if c.Cursor == "" {
		return c.bootstrap(ctx)
	}
	for {
		var page struct {
			Changes []map[string]any `json:"changes"`
			Cursor  string           `json:"cursor"`
			HasMore bool             `json:"has_more"`
		}
		err := c.call(ctx, "GET", "/v1/sync/changes", url.Values{"cursor": {c.Cursor}}, nil, &page)
		var api *apiError
		if errors.As(err, &api) && api.Status == http.StatusGone {
			c.Cursor = ""
			return c.bootstrap(ctx)
		}
		if err != nil {
			return err
		}
		for _, change := range page.Changes {
			c.applyItem(change)
		}
		c.Cursor = page.Cursor
		if !page.HasMore {
			return nil
		}
	}
}

// bootstrap replaces the cloud copy with the server snapshot, then re-applies the unsent outbox on top, so local
// edits stay visible and are pushed later.
func (c *Client) bootstrap(ctx context.Context) error {
	items := map[string]*Item{}
	token := ""
	var cursor string
	for {
		query := url.Values{}
		if token != "" {
			query.Set("page_token", token)
		}
		var page struct {
			Items         []map[string]any `json:"items"`
			NextPageToken string           `json:"next_page_token"`
			Cursor        string           `json:"cursor"`
		}
		if err := c.call(ctx, "GET", "/v1/sync/bootstrap", query, nil, &page); err != nil {
			return err
		}
		previous := c.Items
		c.Items = items
		for _, item := range page.Items {
			c.applyItem(item)
		}
		c.Items = previous
		cursor, token = page.Cursor, page.NextPageToken
		if token == "" {
			break
		}
	}
	c.Items = items
	c.Cursor = cursor
	pending := c.Outbox
	c.Outbox = nil
	for _, m := range pending {
		c.replay(m)
	}
	c.Outbox = pending
	return nil
}

func (c *Client) replay(m tasks.Mutation) {
	k := key(m.ResourceType, m.ResourceID)
	switch m.Action {
	case "create":
		c.Items[k] = &Item{Type: m.ResourceType, ID: m.ResourceID, Data: m.Resource}
	case "patch":
		if item := c.Items[k]; item != nil {
			if merged, err := tasks.MergePatch(item.Data, m.Changes); err == nil {
				item.Data = merged
			}
		}
	case "delete":
		if item := c.Items[k]; item != nil {
			item.Deleted = true
		}
	}
}

func (c *Client) pendingFor(k string) bool {
	for _, m := range c.Outbox {
		if key(m.ResourceType, m.ResourceID) == k {
			return true
		}
	}
	return false
}

func (c *Client) applyItem(item map[string]any) {
	resourceType, _ := item["resource_type"].(string)
	id, _ := item["resource_id"].(string)
	switch item["action"] {
	case "upsert":
		record, _ := item["record"].(map[string]any)
		c.applyRecordWithID(resourceType, id, record, false)
	case "delete":
		version, _ := item["version"].(float64)
		k := key(resourceType, id)
		local := c.Items[k]
		if local == nil {
			local = &Item{Type: resourceType, ID: id}
			c.Items[k] = local
		}
		if int64(version) >= local.Version && !c.pendingFor(k) {
			local.Version, local.Deleted = int64(version), true
		}
	}
}

func (c *Client) applyRecord(resourceType string, record map[string]any, force bool) {
	id, _ := record["id"].(string)
	c.applyRecordWithID(resourceType, id, record, force)
}

// applyRecordWithID installs a server record unless it is older than the local copy or a local edit is pending
// (then the pending edit stays visible and the server decides on push). force applies push results.
func (c *Client) applyRecordWithID(resourceType, id string, record map[string]any, force bool) {
	k := key(resourceType, id)
	var data json.RawMessage
	version := int64(0)
	switch resourceType {
	case "group", "list", "task":
		data, _ = json.Marshal(record[resourceType])
		v, _ := record["version"].(float64)
		version = int64(v)
	case "user_config", "tenant_config":
		data, _ = json.Marshal(record["data"])
		v, _ := record["version"].(float64)
		version = int64(v)
	default: // completion records are immutable
		data, _ = json.Marshal(record)
		version = 1
	}
	local := c.Items[k]
	if !force && local != nil && (local.Version > version || c.pendingFor(k)) {
		return
	}
	c.Items[k] = &Item{Type: resourceType, ID: id, Data: data, Version: version}
}

// Live returns the ids of live items of one type, sorted.
func (c *Client) Live(resourceType string) []string {
	var ids []string
	for _, item := range c.Items {
		if item.Type == resourceType && !item.Deleted {
			ids = append(ids, item.ID)
		}
	}
	sort.Strings(ids)
	return ids
}

func (c *Client) call(ctx context.Context, method, path string, query url.Values, body any, out any) error {
	var reader io.Reader
	if body != nil {
		raw, err := json.Marshal(body)
		if err != nil {
			return err
		}
		reader = bytes.NewReader(raw)
	}
	target := c.BaseURL + path
	if len(query) > 0 {
		target += "?" + query.Encode()
	}
	req, err := http.NewRequestWithContext(ctx, method, target, reader)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+c.Token)
	req.Header.Set("X-Tenant-ID", c.TenantID)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	res, err := c.HTTP.Do(req)
	if err != nil {
		return err
	}
	defer res.Body.Close()
	if res.StatusCode >= 300 {
		var failure struct {
			Error struct {
				Code string `json:"code"`
			} `json:"error"`
		}
		_ = json.NewDecoder(res.Body).Decode(&failure)
		return &apiError{Status: res.StatusCode, Code: failure.Error.Code}
	}
	return json.NewDecoder(res.Body).Decode(out)
}
