using System.Collections.Generic;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Companies;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class CompanyStatsSyncSystem
    {
        private readonly HashSet<Entity> _employeeRemovalMembers = new HashSet<Entity>();
        private readonly Dictionary<Entity, CompanyStatsEmployee> _partialEmployeeStates =
            new Dictionary<Entity, CompanyStatsEmployee>();
        private readonly HashSet<Entity> _partialEmployeeSeen = new HashSet<Entity>();

        private bool ReconcileEmployeeBuffer(Entity company, bool absolute)
        {
            _employeeRemovalScratch.Clear();
            _employeeRemovalMembers.Clear();
            var employees = new BufferEdit<Employee>(EntityManager, company);
            bool changed = false;
            if (absolute)
            {
                for (int i = 0; i < employees.Length; i++)
                {
                    Entity citizen = employees[i].m_Worker;
                    if (_desiredEmployeeEntities.Contains(citizen) ||
                        !_employeeRemovalMembers.Add(citizen)) continue;
                    _employeeRemovalScratch.Add(citizen);
                }

                bool same = employees.Length == _resolvedEmployeeScratch.Count;
                if (same)
                {
                    for (int i = 0; i < employees.Length; i++)
                    {
                        if (employees[i].m_Worker == _resolvedEmployeeScratch[i].Citizen &&
                            employees[i].m_Level == _resolvedEmployeeScratch[i].State.Level)
                            continue;
                        same = false;
                        break;
                    }
                }
                if (same) return false;

                employees.Clear();
                for (int i = 0; i < _resolvedEmployeeScratch.Count; i++)
                {
                    employees.Add(new Employee
                    {
                        m_Worker = _resolvedEmployeeScratch[i].Citizen,
                        m_Level = _resolvedEmployeeScratch[i].State.Level,
                    });
                }
                return true;
            }

            // Index desired residents once, then compact in one pass (repeated searches were quadratic).
            _partialEmployeeStates.Clear();
            _partialEmployeeSeen.Clear();
            for (int i = 0; i < _resolvedEmployeeScratch.Count; i++)
            {
                ResolvedEmployee wanted = _resolvedEmployeeScratch[i];
                _partialEmployeeStates.Add(wanted.Citizen, wanted.State);
            }
            int write = 0;
            int count = employees.Length;
            for (int i = 0; i < count; i++)
            {
                Employee current = employees[i];
                if (_partialEmployeeStates.TryGetValue(current.m_Worker, out CompanyStatsEmployee wanted))
                {
                    if (!_partialEmployeeSeen.Add(current.m_Worker))
                    {
                        changed = true;
                        continue;
                    }
                    if (current.m_Level != wanted.Level)
                    {
                        current.m_Level = wanted.Level;
                        changed = true;
                        employees[write] = current;
                    }
                    else if (write != i) employees[write] = current;
                }
                else if (write != i) employees[write] = current;
                write++;
            }
            while (employees.Length > write) employees.RemoveAt(employees.Length - 1);
            for (int i = 0; i < _resolvedEmployeeScratch.Count; i++)
            {
                ResolvedEmployee wanted = _resolvedEmployeeScratch[i];
                if (_partialEmployeeSeen.Contains(wanted.Citizen)) continue;
                employees.Add(new Employee { m_Worker = wanted.Citizen, m_Level = wanted.State.Level });
                changed = true;
            }
            return changed;
        }
    }
}
