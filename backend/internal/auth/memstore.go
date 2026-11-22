package auth

import (
	"bytes"
	"context"
	"sort"
	"sync"
	"time"
)

// MemoryStore is an in-memory Store for tests. It follows the same contract as the PostgreSQL store; both run the
// conformance suite in internal/auth/storetest.
type MemoryStore struct {
	mu         sync.Mutex
	challenges map[string]*Challenge
	users      map[string]*User
	devices    map[string]*Device
	sessions   map[string]*Session
	AuditLog   []AuditEvent
}

func NewMemoryStore() *MemoryStore {
	return &MemoryStore{
		challenges: map[string]*Challenge{}, users: map[string]*User{}, devices: map[string]*Device{}, sessions: map[string]*Session{},
	}
}

func (m *MemoryStore) CreateChallenge(_ context.Context, c Challenge) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	for _, existing := range m.challenges {
		if existing.EmailNormalized == c.EmailNormalized && existing.ConsumedAt == nil {
			at := c.CreatedAt
			existing.ConsumedAt = &at
		}
	}
	copy := c
	m.challenges[c.ID] = &copy
	return nil
}

func (m *MemoryStore) RecentChallenges(_ context.Context, email string, since time.Time) (int, time.Time, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	count, latest := 0, time.Time{}
	for _, c := range m.challenges {
		if c.EmailNormalized == email && !c.CreatedAt.Before(since) {
			count++
			if c.CreatedAt.After(latest) {
				latest = c.CreatedAt
			}
		}
	}
	return count, latest, nil
}

func (m *MemoryStore) RecentChallengesByIP(_ context.Context, ipHash []byte, since time.Time) (int, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	count := 0
	for _, c := range m.challenges {
		if bytes.Equal(c.RequestIPHash, ipHash) && !c.CreatedAt.Before(since) {
			count++
		}
	}
	return count, nil
}

func (m *MemoryStore) GetChallenge(_ context.Context, id string) (Challenge, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	c, ok := m.challenges[id]
	if !ok {
		return Challenge{}, ErrNotFound
	}
	return *c, nil
}

func (m *MemoryStore) RecordFailedAttempt(_ context.Context, id string) (int, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	c, ok := m.challenges[id]
	if !ok || c.ConsumedAt != nil {
		return 0, ErrChallengeInvalid
	}
	c.AttemptCount++
	return c.AttemptCount, nil
}

func (m *MemoryStore) ConsumeChallenge(_ context.Context, id string, now time.Time, maxAttempts int, newUserID string) (User, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	c, ok := m.challenges[id]
	if !ok || c.ConsumedAt != nil || !now.Before(c.ExpiresAt) || c.AttemptCount >= maxAttempts {
		return User{}, ErrChallengeInvalid
	}
	at := now
	c.ConsumedAt = &at
	for _, u := range m.users {
		if u.EmailNormalized == c.EmailNormalized {
			return *u, nil
		}
	}
	u := &User{ID: newUserID, EmailNormalized: c.EmailNormalized, CreatedAt: now}
	m.users[u.ID] = u
	return *u, nil
}

func (m *MemoryStore) GetUser(_ context.Context, id string) (User, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	u, ok := m.users[id]
	if !ok {
		return User{}, ErrNotFound
	}
	return *u, nil
}

func (m *MemoryStore) CreateDevice(_ context.Context, d Device) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	copy := d
	m.devices[d.ID] = &copy
	return nil
}

func (m *MemoryStore) CreateSession(_ context.Context, s Session) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	copy := s
	m.sessions[s.ID] = &copy
	return nil
}

func (m *MemoryStore) GetSessionByRefreshHash(_ context.Context, hash []byte) (Session, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	for _, s := range m.sessions {
		if bytes.Equal(s.RefreshHash, hash) {
			return *s, nil
		}
	}
	return Session{}, ErrNotFound
}

func (m *MemoryStore) GetSession(_ context.Context, id string) (Session, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	s, ok := m.sessions[id]
	if !ok {
		return Session{}, ErrNotFound
	}
	return *s, nil
}

func (m *MemoryStore) RotateSession(_ context.Context, oldID string, next Session, now time.Time) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	old, ok := m.sessions[oldID]
	if !ok || old.RevokedAt != nil {
		return ErrSessionRotated
	}
	at := now
	old.RevokedAt, old.RevokeReason, old.LastUsedAt = &at, RevokeRotated, now
	copy := next
	m.sessions[next.ID] = &copy
	return nil
}

func (m *MemoryStore) RevokeSession(_ context.Context, id, reason string, now time.Time) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if s, ok := m.sessions[id]; ok {
		revoke(s, reason, now)
	}
	return nil
}

func (m *MemoryStore) RevokeFamily(_ context.Context, familyID, reason string, now time.Time) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	for _, s := range m.sessions {
		if s.TokenFamilyID == familyID {
			revoke(s, reason, now)
		}
	}
	return nil
}

func (m *MemoryStore) RevokeAllForUser(_ context.Context, userID, reason string, now time.Time) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	for _, s := range m.sessions {
		if s.UserID == userID {
			revoke(s, reason, now)
		}
	}
	return nil
}

func revoke(s *Session, reason string, now time.Time) {
	if s.RevokedAt == nil {
		at := now
		s.RevokedAt = &at
	}
	s.RevokeReason = reason
}

func (m *MemoryStore) TouchDevice(_ context.Context, deviceID string, now time.Time) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if d, ok := m.devices[deviceID]; ok {
		d.SeenAt = now
	}
	return nil
}

func (m *MemoryStore) ListDevices(_ context.Context, userID string) ([]Device, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	var devices []Device
	for _, d := range m.devices {
		if d.UserID == userID && d.RevokedAt == nil {
			devices = append(devices, *d)
		}
	}
	sort.Slice(devices, func(i, j int) bool { return devices[i].SeenAt.After(devices[j].SeenAt) })
	return devices, nil
}

func (m *MemoryStore) RevokeDevice(_ context.Context, userID, deviceID string, now time.Time) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	d, ok := m.devices[deviceID]
	if !ok || d.UserID != userID || d.RevokedAt != nil {
		return ErrNotFound
	}
	at := now
	d.RevokedAt = &at
	for _, s := range m.sessions {
		if s.DeviceID == deviceID {
			revoke(s, RevokeDevice, now)
		}
	}
	return nil
}

func (m *MemoryStore) Audit(_ context.Context, e AuditEvent) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.AuditLog = append(m.AuditLog, e)
	return nil
}
