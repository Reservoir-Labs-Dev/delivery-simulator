import React, { useEffect, useMemo, useRef, useState } from 'react';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import './App.css';

// SignalR hub URL — overrideable at build time via REACT_APP_HUB_URL.
// Defaults to localhost:5000/hubs/orders which is what docker-compose
// exposes for the dashboard-api container.
const HUB_URL =
  process.env.REACT_APP_HUB_URL || 'http://localhost:5000/hubs/orders';

const STATUS_TONES = {
  CREATED:              'blue',
  PAYMENT_PROCESSING:   'amber',
  PAYMENT_SUCCEEDED:    'amber',
  PAYMENT_FAILED:       'coral',
  KITCHEN_PREPARING:    'teal',
  ORDER_READY:          'teal',
  DELIVERY_IN_PROGRESS: 'purple',
  DELIVERED:            'accent',
  DELIVERY_FAILED:      'coral',
  DEAD_LETTERED:        'coral',
};

const PIPELINE_SERVICES = [
  { name: 'OrderService',    port: '5294', color: 'blue'   },
  { name: 'PaymentService',  port: '5203', color: 'amber'  },
  { name: 'KitchenService',  port: '5233', color: 'teal'   },
  { name: 'DeliveryService', port: '5097', color: 'purple' },
];

// Maps a pipeline status to its swim-lane column. Statuses we don't
// recognise fall through and are ignored by the grid (still visible in
// the log tab).
const COLUMN_FOR_STATUS = {
  CREATED:              'Order',
  PAYMENT_PROCESSING:   'Payment',
  PAYMENT_SUCCEEDED:    'Payment',
  PAYMENT_FAILED:       'Payment',
  KITCHEN_PREPARING:    'Kitchen',
  ORDER_READY:          'Kitchen',
  DELIVERY_IN_PROGRESS: 'Delivery',
  DELIVERED:            'Delivery',
  DELIVERY_FAILED:      'Delivery',
};

const GRID_COLUMNS = ['Order', 'Payment', 'Kitchen', 'Delivery'];

// Cell tone derivation: prefer the explicit DOG-40 outcome; otherwise
// derive from the status. Terminal-success statuses (PAYMENT_SUCCEEDED,
// ORDER_READY, DELIVERED, CREATED) read as green; in-progress statuses
// (PAYMENT_PROCESSING, KITCHEN_PREPARING, DELIVERY_IN_PROGRESS) read as
// amber; failure statuses + DLQ read as red.
const TERMINAL_SUCCESS_STATUSES = new Set([
  'CREATED',
  'PAYMENT_SUCCEEDED',
  'ORDER_READY',
  'DELIVERED',
]);

function cellTone(cell) {
  if (!cell) return null;
  if (cell.outcome === 'DLQ') return 'red';
  if (cell.outcome === 'FAILED') return 'red';
  if (cell.status === 'PAYMENT_FAILED' || cell.status === 'DELIVERY_FAILED' || cell.status === 'DEAD_LETTERED')
    return 'red';
  if (TERMINAL_SUCCESS_STATUSES.has(cell.status)) return 'green';
  return 'amber';
}

function App() {
  const [connectionState, setConnectionState] = useState('Connecting');
  const [events, setEvents] = useState([]);
  const [activeTab, setActiveTab] = useState('grid'); // 'grid' | 'log'
  const connectionRef = useRef(null);

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl(HUB_URL)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    connectionRef.current = connection;

    connection.on('OrderStatusChanged', (notification) => {
      setEvents((prev) => [
        {
          id: `${notification.orderId}-${notification.updatedAt}-${notification.status}`,
          receivedAt: new Date(),
          ...notification,
        },
        ...prev,
      ].slice(0, 200));
    });

    connection.onreconnecting(() => setConnectionState('Reconnecting'));
    connection.onreconnected(() => setConnectionState('Connected'));
    connection.onclose(() => setConnectionState('Disconnected'));

    connection
      .start()
      .then(() => setConnectionState('Connected'))
      .catch((err) => {
        console.error('SignalR connection failed', err);
        setConnectionState('Disconnected');
      });

    return () => {
      connectionRef.current = null;
      connection.stop().catch(() => { /* swallow on unmount */ });
    };
  }, []);

  const connectionTone = useMemo(() => {
    switch (connectionState) {
      case 'Connected':    return 'ok';
      case 'Reconnecting': return 'warn';
      default:             return 'err';
    }
  }, [connectionState]);

  const ordersSeen = useMemo(
    () => new Set(events.map((e) => e.orderId)).size,
    [events],
  );

  // Build the swim-lane rows. Iterate events oldest-to-newest so the
  // most recent event for each (orderId, column) pair wins.
  const grid = useMemo(() => {
    const byOrder = new Map();
    for (let i = events.length - 1; i >= 0; i--) {
      const e = events[i];
      if (!byOrder.has(e.orderId)) {
        byOrder.set(e.orderId, {
          orderId: e.orderId,
          lastSeenAt: e.receivedAt,
          Order: null,
          Payment: null,
          Kitchen: null,
          Delivery: null,
        });
      }
      const row = byOrder.get(e.orderId);
      if (e.receivedAt >= row.lastSeenAt) row.lastSeenAt = e.receivedAt;
      const col = COLUMN_FOR_STATUS[e.status];
      if (!col) continue;
      row[col] = {
        status: e.status,
        outcome: e.outcome,
        retryCount: e.retryCount || 0,
        updatedAt: e.updatedAt,
      };
    }
    return [...byOrder.values()].sort(
      (a, b) => b.lastSeenAt - a.lastSeenAt,
    );
  }, [events]);

  return (
    <div className="app">
      <header className="header">
        <div className="header-inner">
          <div className="logo">
            <span className={`logo-dot logo-dot--${connectionTone}`} />
            <span className="logo-text">Reservoir Labs</span>
          </div>
          <div className="header-tag">Order Processing Dashboard</div>
        </div>
      </header>

      <main className="main">
        <section className="hero">
          <div className="hero-meta">DOG-41 · swim-lane grid</div>
          <h1 className="hero-title">
            Live order<br />
            <span className="hero-accent">pipeline.</span>
          </h1>
          <p className="hero-desc">
            One row per order, four columns for the four pipeline stages.
            Cells fill in as <code>OrderStatusChanged</code> notifications
            arrive over SignalR. POST against{' '}
            <code>localhost:5294/orders</code> to start a row.
          </p>

          <div className="status-grid">
            <div className="status-item">
              <div className="status-label">React</div>
              <div className="status-value ok">running</div>
            </div>
            <div className="status-item">
              <div className="status-label">SignalR hub</div>
              <div className={`status-value status-value--${connectionTone}`}>
                {connectionState.toLowerCase()}
              </div>
            </div>
            <div className="status-item">
              <div className="status-label">Events received</div>
              <div className="status-value">{events.length}</div>
            </div>
            <div className="status-item">
              <div className="status-label">Distinct orders</div>
              <div className="status-value">{ordersSeen}</div>
            </div>
          </div>
        </section>

        <section className="pipeline">
          <div className="pipeline-label">Pipeline services</div>
          <div className="pipeline-row">
            {PIPELINE_SERVICES.map((svc, i) => (
              <React.Fragment key={svc.name}>
                <div className={`pipeline-node pipeline-node--${svc.color}`}>
                  <div className="pipeline-node-name">{svc.name}</div>
                  <div className="pipeline-node-port">:{svc.port}</div>
                </div>
                {i < PIPELINE_SERVICES.length - 1 && (
                  <div className="pipeline-arrow">→</div>
                )}
              </React.Fragment>
            ))}
          </div>
        </section>

        <section className="events">
          <div className="events-header">
            <div className="events-tabs" role="tablist">
              <button
                type="button"
                role="tab"
                aria-selected={activeTab === 'grid'}
                className={`events-tab ${activeTab === 'grid' ? 'events-tab--active' : ''}`}
                onClick={() => setActiveTab('grid')}
              >
                Grid
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={activeTab === 'log'}
                className={`events-tab ${activeTab === 'log' ? 'events-tab--active' : ''}`}
                onClick={() => setActiveTab('log')}
              >
                Log
              </button>
            </div>
            <button
              type="button"
              className="events-clear"
              onClick={() => setEvents([])}
              disabled={events.length === 0}
            >
              clear
            </button>
          </div>

          {activeTab === 'grid' ? (
            grid.length === 0 ? (
              <div className="events-empty">
                {connectionState === 'Connected'
                  ? 'Waiting for orders… POST one to see a row appear.'
                  : connectionState === 'Reconnecting'
                    ? 'Reconnecting to hub…'
                    : `Not connected. Is dashboard-api running at ${HUB_URL}?`}
              </div>
            ) : (
              <div className="grid" role="table" aria-label="Order swim-lane grid">
                <div className="grid-head" role="row">
                  <div className="grid-head-cell grid-head-cell--id" role="columnheader">Order</div>
                  {GRID_COLUMNS.map((col) => (
                    <div className="grid-head-cell" role="columnheader" key={col}>{col}</div>
                  ))}
                </div>
                {grid.map((row) => (
                  <div className="grid-row" role="row" key={row.orderId}>
                    <div className="grid-cell grid-cell--id" role="cell">
                      {shortOrderId(row.orderId)}
                    </div>
                    {GRID_COLUMNS.map((col) => {
                      const cell = row[col];
                      const tone = cellTone(cell);
                      return (
                        <div className="grid-cell" role="cell" key={col}>
                          {cell ? (
                            <div className={`grid-pill grid-pill--${tone}`} title={cell.status}>
                              <span className="grid-pill-status">{cell.status}</span>
                              {cell.retryCount > 0 && (
                                <span className="grid-pill-retry">×{cell.retryCount}</span>
                              )}
                            </div>
                          ) : (
                            <span className="grid-pill-empty" aria-label="no event yet">—</span>
                          )}
                        </div>
                      );
                    })}
                  </div>
                ))}
              </div>
            )
          ) : (
            events.length === 0 ? (
              <div className="events-empty">
                {connectionState === 'Connected'
                  ? 'Waiting for events… POST an order to see them appear here.'
                  : connectionState === 'Reconnecting'
                    ? 'Reconnecting to hub…'
                    : `Not connected. Is dashboard-api running at ${HUB_URL}?`}
              </div>
            ) : (
              <ul className="event-list">
                {events.map((e) => (
                  <li className="event-row" key={e.id}>
                    <span className="event-time">
                      {e.receivedAt.toLocaleTimeString(undefined, { hour12: false })}
                      .{String(e.receivedAt.getMilliseconds()).padStart(3, '0')}
                    </span>
                    <span className="event-source">{e.sourceService}</span>
                    <span
                      className={`event-status event-status--${STATUS_TONES[e.status] || 'muted'}`}
                    >
                      {e.status}
                    </span>
                    <span className="event-order">{shortOrderId(e.orderId)}</span>
                    {e.retryCount > 0 && (
                      <span className="event-attempt">retry {e.retryCount}</span>
                    )}
                    {e.outcome && e.outcome !== 'SUCCESS' && (
                      <span className={`event-outcome event-outcome--${e.outcome.toLowerCase()}`}>
                        {e.outcome}
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            )
          )}
        </section>
      </main>

      <footer className="footer">
        <span>Reservoir Labs · KIU Bachelor Thesis 2026</span>
        <span>Defense July 2026</span>
      </footer>
    </div>
  );
}

function shortOrderId(orderId) {
  if (!orderId) return '';
  return orderId.replace(/-/g, '').slice(0, 8);
}

export default App;
